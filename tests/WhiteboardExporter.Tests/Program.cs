using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using WhiteboardExporter.Core;
using static FixtureData;

var tests = new List<(string Name, Func<Task> Run)>();
var work = Path.Combine(Path.GetTempPath(), "WhiteboardExporter-CoreTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var normalizer = new FixtureNormalizer();
var importer = new ImportService(normalizer);
var failures = 0;

tests.Add(("公开 JSON 导入、中文、负坐标、旋转和分组", async () =>
{
    var scene = NewScene();
    scene["elements"] = new JsonArray(new JsonObject
    {
        ["id"] = "group", ["kind"] = "group", ["z"] = 1,
        ["bounds"] = new JsonObject { ["x"] = -100, ["y"] = -30, ["width"] = 500, ["height"] = 200 },
        ["transform"] = new JsonArray(0.8660254, 0.5, -0.5, 0.8660254, -100, 30),
        ["children"] = new JsonArray(new JsonObject { ["id"] = "文字", ["kind"] = "text", ["text"] = "离线白板重绘" })
    });
    var path = Path.Combine(work, "中文.json");
    await File.WriteAllTextAsync(path, scene.ToJsonString());
    var result = await importer.ImportAsync(path);
    Assert(result.Title == "中文测试白板" && result.ElementCount == 1 && result.Status == "complete", "JSON 场景内容失真");
}));
tests.Add(("嵌入资源分离、哈希命名、临时重绘 data URL", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    AddEmbeddedImage(document.Scene);
    AssetService.ExtractEmbeddedAssets(document);
    Assert(document.Assets.Count == 1, "未分离嵌入图片");
    Assert(document.Scene["assets"]!["image"]!["dataUrl"] is null, "持久化 JSON 仍包含 data URL");
    Assert(document.Scene["assets"]!["image"]!["path"]!.GetValue<string>().StartsWith("assets/"), "资源路径没有规范化");
    var hydrated = AssetService.HydrateScene(document);
    Assert(hydrated["assets"]!["image"]!["dataUrl"]!.GetValue<string>().StartsWith("data:image/png;base64,"), "重绘资源没有恢复");
    Assert(document.Scene["assets"]!["image"]!["dataUrl"] is null, "临时渲染修改了源 JSON");
    await Task.CompletedTask;
}));
tests.Add(("备份往返保持原始 ZIP 字节及资源", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    AddEmbeddedImage(document.Scene);
    document.Originals["source/microsoft-export.zip"] = [1, 2, 3, 0, 255];
    var path = Path.Combine(work, "roundtrip.wbarchive");
    await ArchiveService.SaveAsync(document, path);
    var result = await importer.ImportAsync(path);
    Assert(result.Status == "complete" && result.Assets.Count == 1, "往返丢失资源或状态");
    Assert(result.Originals["source/microsoft-export.zip"].SequenceEqual(document.Originals["source/microsoft-export.zip"]), "原始文件发生变化");
    Assert(document.Scene["assets"]!["image"]!["dataUrl"] is not null, "保存操作改变调用方原始对象");
}));
tests.Add(("JSON 与相邻资源可独立再次导入", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    AddEmbeddedImage(document.Scene);
    var path = Path.Combine(work, "json-export", "scene.json");
    await ArchiveService.SaveJsonAsync(document, path);
    var result = await importer.ImportAsync(path);
    Assert(result.Assets.Count == 1 && result.Status == "complete", "JSON 配套资源没有恢复");
    Assert(!File.ReadAllText(path).Contains("data:image"), "JSON 未移除临时编码");
}));
tests.Add(("重新导出 JSON 原子修复损坏的目标资源", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    AddEmbeddedImage(document.Scene);
    AssetService.ExtractEmbeddedAssets(document);
    var path = Path.Combine(work, "repair-resource", "scene.json");
    await ArchiveService.SaveJsonAsync(document, path);
    var relative = document.Scene["assets"]!["image"]!["path"]!.GetValue<string>();
    var assetPath = Path.Combine(Path.GetDirectoryName(path)!, relative.Replace('/', Path.DirectorySeparatorChar));
    var original = document.Assets[relative];
    var corrupted = (byte[])original.Clone(); corrupted[^1] ^= 0xFF;
    await File.WriteAllBytesAsync(assetPath, corrupted);
    await ArchiveService.SaveJsonAsync(document, path);
    var result = await importer.ImportAsync(path);
    Assert(result.Status == "complete" && result.Assets.Values.Single().SequenceEqual(original), "同长度损坏资源未恢复正确字节");
    await File.WriteAllBytesAsync(assetPath, [1, 2]);
    await ArchiveService.SaveJsonAsync(document, path);
    Assert((await importer.ImportAsync(path)).Assets.Values.Single().SequenceEqual(original), "截断资源未恢复正确字节");
    Assert(!Directory.GetFiles(Path.GetDirectoryName(assetPath)!).Any(name => name.EndsWith(".tmp", StringComparison.Ordinal)), "修复留下部分临时目标");
}));
tests.Add(("微软 ZIP 读取画布 HTML 与评论，保留原包", async () =>
{
    normalizer.NextScene = NewScene();
    var bytes = Zip(("Export/Whiteboard.html", Encoding.UTF8.GetBytes("<div id='canvasContent'>中文</div>")), ("Export/comments.json", "{\"threads\":[]}"u8.ToArray()));
    var path = Path.Combine(work, "microsoft.zip");
    await File.WriteAllBytesAsync(path, bytes);
    var result = await importer.ImportAsync(path);
    Assert(normalizer.LastHtml!.Contains("中文") && normalizer.LastComments == "{\"threads\":[]}", "正规化器未收到源数据");
    Assert(result.Originals["source/microsoft-export.zip"].SequenceEqual(bytes), "未原样保留微软 ZIP");
}));
tests.Add(("ZIP 相对图片资源按 HTML 目录解析", async () =>
{
    var scene = NewScene();
    scene["assets"]!["image"] = new JsonObject { ["path"] = "assets/image.png", ["source"] = "images/diagram.png", ["mime"] = "image/png" };
    scene["elements"] = new JsonArray(new JsonObject { ["id"] = "image-element", ["kind"] = "image", ["assetId"] = "image" });
    normalizer.NextScene = scene;
    var png = Convert.FromBase64String(PngBase64);
    var path = Path.Combine(work, "relative.zip");
    await File.WriteAllBytesAsync(path, Zip(("board/index.html", "<div id='canvasContent'></div>"u8.ToArray()), ("board/images/diagram.png", png)));
    var result = await importer.ImportAsync(path);
    Assert(result.Assets.Count == 1 && result.Assets.Values.Single().SequenceEqual(png) && result.Status == "complete", "HTML 相对图片没有正确导入");
}));
tests.Add(("评论 JSON 或截图不能冒充完整白板", async () =>
{
    var path = Path.Combine(work, "metadata-only.zip");
    await File.WriteAllBytesAsync(path, Zip(("comments.json", "{}"u8.ToArray()), ("preview.png", Convert.FromBase64String(PngBase64))));
    await Throws<InvalidDataException>(() => importer.ImportAsync(path));
}));
tests.Add(("损坏 ZIP 拒绝导入", async () =>
{
    var path = Path.Combine(work, "broken.zip");
    await File.WriteAllBytesAsync(path, [0x50, 0x4b, 0, 1, 2]);
    await Throws<InvalidDataException>(() => importer.ImportAsync(path));
}));
tests.Add(("ZIP 路径穿越拒绝", async () =>
{
    foreach (var dangerous in new[] { "../escape.txt", "folder/../../escape.txt", "C:/escape.txt", "/escape.txt", "folder\\..\\escape.txt" })
    {
        var path = Path.Combine(work, "traversal.zip");
        await File.WriteAllBytesAsync(path, Zip((dangerous, "bad"u8.ToArray()), ("index.html", "<div id='canvasContent'></div>"u8.ToArray())));
        await Throws<InvalidDataException>(() => importer.ImportAsync(path));
    }
}));
tests.Add(("ZIP 大小写和分隔符别名冲突拒绝", async () =>
{
    foreach (var pair in new[] { ("index.html", "INDEX.HTML"), ("folder/index.html", "folder\\index.html"), ("folder", "folder/index.html") })
    {
        var path = Path.Combine(work, "duplicate.zip");
        await File.WriteAllBytesAsync(path, Zip((pair.Item1, "<div id='canvasContent'></div>"u8.ToArray()), (pair.Item2, "<div id='canvasContent'></div>"u8.ToArray())));
        await Throws<InvalidDataException>(() => importer.ImportAsync(path));
    }
}));
tests.Add(("ZIP 解压炸弹拒绝", async () =>
{
    var path = Path.Combine(work, "bomb.zip");
    await File.WriteAllBytesAsync(path, Zip(("zeros.bin", new byte[3 * 1024 * 1024]), ("index.html", "<div id='canvasContent'></div>"u8.ToArray())));
    await Throws<InvalidDataException>(() => importer.ImportAsync(path));
}));
tests.Add(("SHA-256 发现备份篡改", async () =>
{
    var path = Path.Combine(work, "tampered.wbarchive");
    await ArchiveService.SaveAsync(new BoardDocument { Scene = NewScene() }, path);
    using (var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
    using (var archive = new ZipArchive(file, ZipArchiveMode.Update))
    {
        archive.GetEntry("scene.json")!.Delete();
        using var stream = archive.CreateEntry("scene.json").Open();
        stream.Write(Encoding.UTF8.GetBytes(NewScene("已篡改").ToJsonString()));
    }
    await Throws<InvalidDataException>(() => importer.ImportAsync(path));
}));
tests.Add(("缺失和远程资源明确为 partial 且不联网", async () =>
{
    var scene = NewScene();
    scene["assets"]!["remote"] = new JsonObject { ["path"] = "assets/missing.png", ["mime"] = "image/png", ["source"] = "https://invalid.invalid/never-request.png" };
    scene["elements"] = new JsonArray(new JsonObject { ["id"] = "remote-element", ["kind"] = "image", ["assetId"] = "remote" }, new JsonObject { ["id"] = "undeclared-element", ["kind"] = "image", ["assetId"] = "undeclared" });
    var path = Path.Combine(work, "missing.json");
    await File.WriteAllTextAsync(path, scene.ToJsonString());
    var document = await importer.ImportAsync(path);
    Assert(document.Status == "partial" && document.Scene["diagnostics"]!["missingAssets"]!.AsArray().Count == 2, "缺失资源未反映在状态中");
    Assert(document.Assets.Count == 0, "不应远程加载资源");
}));
tests.Add(("未知对象与原始片段可往返保留", async () =>
{
    var scene = NewScene();
    scene["elements"] = new JsonArray(new JsonObject { ["id"] = "unknown", ["kind"] = "unknown", ["raw"] = "<future-widget data-version='3'>未识别</future-widget>" });
    scene["diagnostics"]!["unsupported"] = 1;
    scene["diagnostics"]!["supported"] = 0;
    var path = Path.Combine(work, "unknown.wbarchive");
    await ArchiveService.SaveAsync(new BoardDocument { Scene = scene }, path);
    var document = await importer.ImportAsync(path);
    Assert(document.Status == "partial" && document.Scene["elements"]![0]!["raw"]!.GetValue<string>().Contains("future-widget"), "未知对象丢失");
}));
tests.Add(("JSON 畸形、版本、坐标、变换和重复元素验证", async () =>
{
    var path = Path.Combine(work, "invalid.json");
    await File.WriteAllTextAsync(path, "{ definitely not json");
    await Throws<InvalidDataException>(() => importer.ImportAsync(path));
    foreach (var change in new Action<JsonObject>[]
    {
        scene => scene["schemaVersion"] = 99,
        scene => scene["bounds"]!["width"] = -1,
        scene => scene["bounds"]!["x"] = 1e99,
        scene => scene["elements"] = new JsonArray(new JsonObject { ["id"] = "x", ["kind"] = "text", ["transform"] = new JsonArray(1, 0, 0) }),
        scene => scene["elements"] = new JsonArray(new JsonObject { ["id"] = "x", ["kind"] = "text" }, new JsonObject { ["id"] = "x", ["kind"] = "shape" }),
        scene => scene["assets"]!["x"] = new JsonObject { ["path"] = "../outside.png" }
    })
    {
        var scene = NewScene(); change(scene);
        await File.WriteAllTextAsync(path, scene.ToJsonString());
        await Throws<InvalidDataException>(() => importer.ImportAsync(path));
    }
}));
tests.Add(("已取消操作保留旧目标且无临时文件", async () =>
{
    var directory = Path.Combine(work, "cancel"); Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "existing.wbarchive"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws<OperationCanceledException>(() => ArchiveService.SaveAsync(new BoardDocument { Scene = NewScene() }, path, canceled.Token));
    Assert(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }) && Directory.GetFiles(directory).Length == 1, "取消覆盖目标或留下临时文件");
    await Throws<OperationCanceledException>(() => importer.ImportAsync(path, canceled.Token));
    await ArchiveService.SaveAsync(new BoardDocument { Scene = NewScene() }, path);
    Assert((await importer.ImportAsync(path)).Title == "中文测试白板", "取消后不能重试");
}));
tests.Add(("重复 JSON 属性拒绝而不采用最后一个值", async () =>
{
    var path = Path.Combine(work, "duplicate-property.json");
    var scene = NewScene().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":99,\"schemaVersion\":1", StringComparison.Ordinal);
    await File.WriteAllTextAsync(path, scene);
    await Throws<InvalidDataException>(() => importer.ImportAsync(path));
}));
tests.Add(("解析警告不会被完整资源覆盖", async () =>
{
    var scene = NewScene();
    scene["diagnostics"]!["status"] = "partial";
    scene["diagnostics"]!["items"] = new JsonArray(new JsonObject { ["code"] = "COMMENTS_INVALID", ["severity"] = "warning", ["message"] = "评论 JSON 无法解析" });
    var document = new BoardDocument { Scene = scene };
    AssetService.ExtractEmbeddedAssets(document);
    Assert(document.Status == "partial", "未解决的解析警告被标为成功");
    await Task.CompletedTask;
}));
tests.Add(("原生 .whiteboard 保留数据并明确无法重绘", async () =>
{
    var path = Path.Combine(work, "native.whiteboard"); var bytes = new byte[] { 7, 8, 9 };
    await File.WriteAllBytesAsync(path, bytes);
    var document = await importer.ImportAsync(path);
    Assert(document.Status == "failed" && document.Originals["source/native.whiteboard"].SequenceEqual(bytes), "原生文件状态或数据错误");
    var backup = Path.Combine(work, "native.wbarchive");
    await ArchiveService.SaveAsync(document, backup);
    Assert((await importer.ImportAsync(backup)).Status == "failed", "不支持的原生文件被标为成功");
}));
tests.Add(("取消并重启相同批次保留无修改时间白板的签名", async () =>
{
    var state = new JsonObject();
    var firstRun = ExportTaskState.Begin(state, ["board-b", "board-a", "board-a"], "Archive,Json|4096");
    var signature = ExportTaskState.CloudSignature(null, firstRun);
    state["completed-board-a"] = new JsonObject { ["signature"] = signature, ["status"] = "complete" };
    // Persisted JSON models a process interruption; the task remains running.
    var restored = JsonNode.Parse(state.ToJsonString())!.AsObject();
    var resumedRun = ExportTaskState.Begin(restored, ["board-a", "board-b"], "Archive,Json|4096");
    Assert(firstRun == resumedRun && ExportTaskState.CloudSignature(null, resumedRun) == signature, "重启取消的同一批次生成了新签名");
    Assert(restored["completed-board-a"]!["status"]!.GetValue<string>() == "complete", "恢复任务丢失已完成板记录");
    Assert(restored["$task"]!["selectedIds"]!.AsArray().Count == 2 && restored["$task"]!["selectedIds"]![0]!.GetValue<string>() == "board-a", "批次选择没有去重排序");
    await Task.CompletedTask;
}));
tests.Add(("已完成任务及改变选择或设置启动新批次", async () =>
{
    var state = new JsonObject();
    var initial = ExportTaskState.Begin(state, ["board-a"], "Pdf|4096");
    ExportTaskState.Complete(state);
    Assert(state["$task"]!["status"]!.GetValue<string>() == "done", "完成状态未保存");
    var newRun = ExportTaskState.Begin(state, ["board-a"], "Pdf|4096");
    Assert(newRun != initial, "主动重新导出错误复用已完成批次");
    var changedSettings = ExportTaskState.Begin(state, ["board-a"], "Pdf|8192");
    Assert(changedSettings != newRun, "设置改变没有新批次");
    var changedSelection = ExportTaskState.Begin(state, ["board-a", "board-b"], "Pdf|8192");
    Assert(changedSelection != changedSettings, "选择改变没有新批次");
    state["$task"]!["runId"] = 123;
    Assert(ExportTaskState.Begin(state, ["board-a", "board-b"], "Pdf|8192") != changedSelection, "损坏的批次记录被复用");
    foreach (var corruption in new Action<JsonObject>[]
    {
        record => record["$task"] = new JsonArray(1, 2),
        record => record["$task"]!["taskKey"] = new JsonObject(),
        record => record["$task"]!["runId"] = "invalid-id",
        record => record["$task"]!["status"] = 123,
        record => record["$task"]!["settingsKey"] = false,
        record => record["$task"]!["selectedIds"] = new JsonArray(1)
    })
    {
        var previous = ExportTaskState.Begin(state, ["board-a", "board-b"], "Pdf|8192");
        corruption(state);
        Assert(ExportTaskState.Begin(state, ["board-a", "board-b"], "Pdf|8192") != previous, "字段损坏没有安全启动新批次");
    }
    await Task.CompletedTask;
}));
tests.Add(("已知修改时间签名使用 UTC 且不受批次影响", async () =>
{
    var timestamp = DateTimeOffset.Parse("2026-10-08T18:00:00+08:00");
    Assert(ExportTaskState.CloudSignature(timestamp, "run-a") == ExportTaskState.CloudSignature(timestamp.ToUniversalTime(), "run-b"), "相同修改时间因时区或批次变化产生不同签名");
    Assert(ExportTaskState.CloudSignature(timestamp.AddSeconds(1), "run-a") != ExportTaskState.CloudSignature(timestamp, "run-a"), "修改时间变化没有更新签名");
    Assert(ExportTaskState.CloudSignature(null, "run-a") != ExportTaskState.CloudSignature(null, "run-b"), "缺失修改时间的新任务重复使用旧签名");
    await Task.CompletedTask;
}));
tests.Add(("微软编辑时间只接受明确时区或明确 Unix 单位", async () =>
{
    foreach (var timestamp in new[] { "2026-10-08T18:34:12+08:00", "2026-10-08T10:34:12Z", "2026-10-08T10:34:12.1234567+00:00" })
    {
        using var json = System.Text.Json.JsonDocument.Parse("{\"lastModifiedDateTime\":\"" + timestamp + "\"}");
        var metadata = BoardMetadata.ReadMicrosoftLastModified(json.RootElement);
        Assert(metadata.Modified?.Offset == TimeSpan.Zero && metadata.Source == "microsoft-api:lastModifiedDateTime", "明确时区时间未被规范化");
    }
    foreach (var timestamp in new[] { "2026-10-08T18:34:12", "2026-10-08", "3 小时前", "yesterday", "2026-13-08T10:34:12Z" })
    {
        using var json = System.Text.Json.JsonDocument.Parse("{\"lastModifiedTime\":\"" + timestamp + "\"}");
        Assert(BoardMetadata.ReadMicrosoftLastModified(json.RootElement).Modified is null, "没有权威时区的文字被误判为编辑时间");
    }
    using var seconds = System.Text.Json.JsonDocument.Parse("{\"modifiedTimeUnixSeconds\":1791453600}");
    using var milliseconds = System.Text.Json.JsonDocument.Parse("{\"lastModifiedUnixMilliseconds\":\"1791453600123\"}");
    Assert(BoardMetadata.ReadMicrosoftLastModified(seconds.RootElement).Modified == DateTimeOffset.FromUnixTimeSeconds(1791453600), "Unix 秒解析错误");
    Assert(BoardMetadata.ReadMicrosoftLastModified(milliseconds.RootElement).Modified == DateTimeOffset.FromUnixTimeMilliseconds(1791453600123), "Unix 毫秒解析错误");
    using var ambiguous = System.Text.Json.JsonDocument.Parse("{\"lastModifiedTime\":1791453600123,\"modifiedTimeUnixSeconds\":9223372036854775807}");
    Assert(BoardMetadata.ReadMicrosoftLastModified(ambiguous.RootElement).Modified is null, "单位不明或超范围时间被猜测为有效");
    await Task.CompletedTask;
}));
tests.Add(("编辑时间在 JSON、备份清单和再导入中保留且不使用文件时间", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    var modified = DateTimeOffset.Parse("2026-10-08T18:34:12+08:00");
    BoardMetadata.SetLastModified(document, modified, "microsoft-api:lastModifiedDateTime");
    BoardMetadata.SetCaptured(document, modified.AddDays(1));
    BoardMetadata.SetLastModified(document, null);
    var archivePath = Path.Combine(work, "metadata.wbarchive");
    await ArchiveService.SaveAsync(document, archivePath);
    using (var archive = ZipFile.OpenRead(archivePath))
    using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
    {
        var manifest = JsonNode.Parse(await reader.ReadToEndAsync())!;
        Assert(manifest["lastModifiedUtc"]!.GetValue<string>() == modified.ToUniversalTime().ToString("O"), "备份清单没有真实编辑时间");
        Assert(manifest["capturedUtc"]!.GetValue<string>() != manifest["lastModifiedUtc"]!.GetValue<string>(), "采集时间替代了源编辑时间");
    }
    File.SetLastWriteTimeUtc(archivePath, DateTime.UtcNow);
    var restored = await importer.ImportAsync(archivePath);
    Assert(restored.LastModifiedUtc == modified.ToUniversalTime(), "导入以文件时间覆盖源编辑时间");
    var jsonPath = Path.Combine(work, "metadata.json");
    await ArchiveService.SaveJsonAsync(restored, jsonPath);
    Assert((await importer.ImportAsync(jsonPath)).LastModifiedUtc == modified.ToUniversalTime(), "公开 JSON 丢失编辑时间");
    Assert(new BoardDocument { Scene = NewScene() }.LastModifiedUtc is null, "未知源编辑时间被填成当前时间");
    var invalid = NewScene(); invalid["board"]!["lastModifiedUtc"] = "2026-10-08 18:00";
    await Throws<InvalidDataException>(() => { SceneValidation.Validate(invalid); return Task.CompletedTask; });
}));
tests.Add(("场景指纹忽略采集时钟及属性顺序但保留编辑时间和资源变化", async () =>
{
    var first = new BoardDocument { Scene = NewScene() };
    first.Assets["assets/a.png"] = [1, 2];
    BoardMetadata.SetCaptured(first, DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
    var second = new BoardDocument { Scene = new JsonObject(first.Scene.Reverse().Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))) };
    second.Assets["assets/a.png"] = [1, 2];
    BoardMetadata.SetCaptured(second, DateTimeOffset.Parse("2026-10-09T00:00:00Z"));
    Assert(ExportTaskState.SceneFingerprint(first) == ExportTaskState.SceneFingerprint(second), "采集时钟或无意义对象顺序产生了新版本");
    Assert(ExportTaskState.DocumentFingerprint(first) != ExportTaskState.DocumentFingerprint(second), "精确完整指纹不应忽略元数据");
    BoardMetadata.SetLastModified(second, DateTimeOffset.Parse("2026-10-08T02:00:00Z"));
    Assert(ExportTaskState.SceneFingerprint(first) != ExportTaskState.SceneFingerprint(second), "编辑日期修改被错误忽略");
    second.Scene["board"]!.AsObject().Remove("lastModifiedUtc"); second.Assets["assets/a.png"] = [1, 3];
    Assert(ExportTaskState.SceneFingerprint(first) != ExportTaskState.SceneFingerprint(second), "资源内容修改被错误忽略");
    await Task.CompletedTask;
}));
tests.Add(("重复备份比较忽略容器时间并校验旧资源和原包", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    AddEmbeddedImage(document.Scene); document.Originals["source/microsoft-export.zip"] = [1, 2, 3];
    BoardMetadata.SetCaptured(document, DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
    var path = Path.Combine(work, "duplicate.wbarchive");
    await ArchiveService.SaveAsync(document, path);
    BoardMetadata.SetCaptured(document, DateTimeOffset.Parse("2026-10-09T00:00:00Z"));
    Assert(await ArchiveService.MatchesDocumentAsync(document, path), "仅重新采集相同数据产生多余备份版本");
    document.Originals["source/microsoft-export.zip"] = [1, 2, 4];
    Assert(!await ArchiveService.MatchesDocumentAsync(document, path), "源包变化但场景相同时被吞掉");
    document.Originals["source/microsoft-export.zip"] = [1, 2, 3];
    using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
    {
        var entry = archive.Entries.Single(e => e.FullName.StartsWith("assets/"));
        var name = entry.FullName; entry.Delete();
        using var stream = archive.CreateEntry(name).Open(); stream.Write([1, 2]);
    }
    Assert(!await ArchiveService.MatchesDocumentAsync(document, path), "损坏旧资源被判为无需重新导出");
    await File.WriteAllTextAsync(path, "broken zip");
    Assert(!await ArchiveService.MatchesDocumentAsync(document, path), "损坏备份未允许安全重新生成");
}));
tests.Add(("重复 JSON 比较校验相邻资源而不忽略内容修改", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    AddEmbeddedImage(document.Scene);
    BoardMetadata.SetCaptured(document, DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
    var path = Path.Combine(work, "duplicate-json", "scene.json");
    await ArchiveService.SaveJsonAsync(document, path);
    BoardMetadata.SetCaptured(document, DateTimeOffset.Parse("2026-10-09T00:00:00Z"));
    Assert(await ArchiveService.SceneJsonMatchesAsync(document, path), "相同场景因采集时钟而产生多余 JSON 版本");
    document.Originals["responses/new.json"] = "new source response"u8.ToArray();
    Assert(await ArchiveService.SceneJsonMatchesAsync(document, path), "未进入公开 JSON 的原始响应影响 JSON 比较");
    document.Scene["board"]!["title"] = "新标题";
    Assert(!await ArchiveService.SceneJsonMatchesAsync(document, path), "标题变更没有生成新 JSON");
    document.Scene["board"]!["title"] = "中文测试白板";
    var assetPath = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "assets")).Single();
    await File.WriteAllBytesAsync(assetPath, [1, 2]);
    Assert(!await ArchiveService.SceneJsonMatchesAsync(document, path), "损坏资源导致误跳过重复 JSON 导出");
    await ArchiveService.SaveJsonAsync(document, path);
    File.Delete(assetPath);
    Assert(!await ArchiveService.SceneJsonMatchesAsync(document, path), "缺失资源导致误跳过重复 JSON 导出");
}));
tests.Add(("重复数据检查传播取消且不改写目标", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    var path = Path.Combine(work, "comparison-cancel.wbarchive");
    await ArchiveService.SaveAsync(document, path);
    var bytes = await File.ReadAllBytesAsync(path);
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws<OperationCanceledException>(() => ArchiveService.MatchesDocumentAsync(document, path, canceled.Token));
    await Throws<OperationCanceledException>(() => ArchiveService.SceneJsonMatchesAsync(document, path, canceled.Token));
    var after = await File.ReadAllBytesAsync(path);
    Assert(bytes.SequenceEqual(after), "数据检查修改了现有导出文件");
}));
tests.Add(("文档指纹包含原始包和资源字节且与目录插入顺序无关", async () =>
{
    var first = new BoardDocument { Scene = NewScene() };
    first.Assets["assets/a.png"] = [1, 2]; first.Assets["assets/b.png"] = [3, 4];
    first.Originals["source/export.zip"] = [5, 6]; first.Originals["source/comments.json"] = [7, 8];
    var second = new BoardDocument { Scene = (JsonObject)first.Scene.DeepClone() };
    second.Assets["assets/b.png"] = [3, 4]; second.Assets["assets/a.png"] = [1, 2];
    second.Originals["source/comments.json"] = [7, 8]; second.Originals["source/export.zip"] = [5, 6];
    var fingerprint = ExportTaskState.DocumentFingerprint(first);
    Assert(fingerprint == ExportTaskState.DocumentFingerprint(second), "集合插入顺序影响指纹");
    second.Originals["source/export.zip"] = [5, 9];
    Assert(fingerprint != ExportTaskState.DocumentFingerprint(second), "原始包变化但场景相同时仍被判为已完成");
    second.Originals["source/export.zip"] = [5, 6]; second.Assets["assets/a.png"] = [1, 9];
    Assert(fingerprint != ExportTaskState.DocumentFingerprint(second), "图片内容变化未更新指纹");
    second.Assets["assets/a.png"] = [1, 2]; second.Scene["board"]!["title"] = "修改后的白板";
    Assert(fingerprint != ExportTaskState.DocumentFingerprint(second), "场景 JSON 变化未更新指纹");
    await Task.CompletedTask;
}));
tests.Add(("旧失败备份从原 ZIP 重新解析并保留身份及全部源字节", async () =>
{
    var scene = FailedScene();
    scene["board"]!["id"] = "archived-cloud-id"; scene["board"]!["title"] = "旧版失败白板";
    var document = new BoardDocument { Scene = scene };
    BoardMetadata.SetLastModified(document, DateTimeOffset.Parse("2026-10-07T02:03:04Z"), "microsoft-api:lastModifiedTime");
    var sourceZip = Zip(("board/index.html", "<div id='canvasContent'>zip-canvas</div>"u8.ToArray()), ("board/comments.json", "{\"commentThreads\":[]}"u8.ToArray()));
    document.Originals["source/microsoft-export.zip"] = sourceZip;
    document.Originals["source/export.html"] = "<div id='canvasContent'>stale-html</div>"u8.ToArray();
    document.Originals["source/extra-response.json"] = "{\"testOnly\":true}"u8.ToArray();
    var path = Path.Combine(work, "recover-zip.wbarchive");
    await ArchiveService.SaveAsync(document, path);
    var before = await File.ReadAllBytesAsync(path);
    var fixedParser = new FixtureNormalizer { NextScene = NewScene("解析器回退标题") };
    fixedParser.NextScene["elements"] = new JsonArray(new JsonObject { ["id"] = "recovered-element", ["kind"] = "shape", ["svg"] = "<rect width='50' height='50'/>" });
    var result = await new ImportService(fixedParser).ImportAsync(path);
    Assert(result.Status == "complete" && result.ElementCount == 1, "旧失败缓存没有重新解析");
    Assert(result.LastModifiedUtc == document.LastModifiedUtc && result.Scene["board"]!["lastModifiedSource"]!.GetValue<string>() == "microsoft-api:lastModifiedTime", "从源包重解析时丢失原始编辑时间");
    Assert(result.Id == "archived-cloud-id" && result.Title == "旧版失败白板", "恢复更改了白板身份");
    Assert(fixedParser.LastId == result.Id && fixedParser.LastTitle == result.Title && fixedParser.LastHtml!.Contains("zip-canvas"), "没有优先使用原 ZIP 和原白板身份");
    foreach (var pair in document.Originals) Assert(result.Originals[pair.Key].SequenceEqual(pair.Value), "重新解析改写或丢失源文件");
    Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(before), "只读导入改写了源备份");
}));
tests.Add(("仅保留 HTML 的失败备份恢复评论与已有离线图片", async () =>
{
    var document = new BoardDocument { Scene = FailedScene() };
    document.Scene["assets"]!["old-image-id"] = new JsonObject { ["path"] = "assets/original.png", ["mime"] = "image/png", ["source"] = "https://example.invalid/captured-image.png", ["dataUrl"] = "data:image/png;base64," + PngBase64 };
    document.Originals["source/export.html"] = "<div id='canvasContent'>html-only</div>"u8.ToArray();
    document.Originals["source/comments.json"] = "{\"commentThreads\":[]}"u8.ToArray();
    var path = Path.Combine(work, "recover-html.wbarchive"); await ArchiveService.SaveAsync(document, path);
    var recoveredScene = NewScene();
    recoveredScene["assets"]!["new-image-id"] = new JsonObject { ["path"] = "assets/new-image.png", ["mime"] = "image/png", ["source"] = "https://example.invalid/captured-image.png" };
    recoveredScene["elements"] = new JsonArray(new JsonObject { ["id"] = "image", ["kind"] = "image", ["assetId"] = "new-image-id" });
    var fixedParser = new FixtureNormalizer { NextScene = recoveredScene };
    var result = await new ImportService(fixedParser).ImportAsync(path);
    Assert(fixedParser.LastComments == "{\"commentThreads\":[]}" && fixedParser.LastHtml!.Contains("html-only"), "HTML 恢复丢失评论输入");
    var restoredPath = result.Scene["assets"]!["new-image-id"]!["path"]!.GetValue<string>();
    Assert(result.Status == "complete" && result.Assets[restoredPath].SequenceEqual(Convert.FromBase64String(PngBase64)), "解析器重新编号后没有按来源恢复已保存图片");
}));
tests.Add(("当前解析器确认的合法空白板可从旧失败缓存恢复", async () =>
{
    var document = new BoardDocument { Scene = FailedScene() };
    document.Originals["source/microsoft-export.zip"] = Zip(("index.html", "<div id='canvasContent'></div>"u8.ToArray()));
    var path = Path.Combine(work, "recover-empty.wbarchive"); await ArchiveService.SaveAsync(document, path);
    var result = await new ImportService(new FixtureNormalizer { NextScene = NewScene() }).ImportAsync(path);
    Assert(result.Status == "complete" && result.ElementCount == 0, "合法空白板被旧失败状态阻断");
}));
tests.Add(("失败备份重解析按来源哈希恢复脱敏图片且不重新写入签名网址", async () =>
{
    const string source = "https://example.invalid/private-image.png?sig=synthetic-test-only";
    var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    foreach (var variant in new[] { "old-hash-new-url", "both-hash", "old-url-new-hash" })
    {
        var document = new BoardDocument { Scene = FailedScene() };
        var previous = new JsonObject { ["path"] = "assets/original.png", ["mime"] = "image/png", ["dataUrl"] = "data:image/png;base64," + PngBase64 };
        previous[variant == "old-url-new-hash" ? "source" : "sourceHash"] = variant == "old-url-new-hash" ? source : hash.ToUpperInvariant();
        document.Scene["assets"]!["old-id"] = previous;
        document.Originals["source/export.html"] = "<div id='canvasContent'>synthetic recovery</div>"u8.ToArray();
        var path = Path.Combine(work, variant + ".wbarchive"); await ArchiveService.SaveAsync(document, path);
        var scene = NewScene();
        var current = new JsonObject { ["path"] = "assets/new.png", ["mime"] = "image/png" };
        current[variant == "old-hash-new-url" ? "source" : "sourceHash"] = variant == "old-hash-new-url" ? source : hash;
        scene["assets"]!["new-id"] = current;
        scene["elements"] = new JsonArray(new JsonObject { ["id"] = "image", ["kind"] = "image", ["assetId"] = "new-id" });
        var restored = await new ImportService(new FixtureNormalizer { NextScene = scene }).ImportAsync(path);
        var asset = restored.Scene["assets"]!["new-id"]!;
        Assert(restored.Status == "complete" && restored.Assets[asset["path"]!.GetValue<string>()].SequenceEqual(Convert.FromBase64String(PngBase64)), "来源哈希没有恢复资源：" + variant);
        Assert(asset["source"] is null && asset["sourceHash"]!.GetValue<string>() == hash, "恢复重新暴露已脱敏来源或未保留匹配哈希");
    }
}));
tests.Add(("不同来源哈希不得绑定图片，损坏的可选哈希字段不阻塞重解析", async () =>
{
    var document = new BoardDocument { Scene = FailedScene() };
    document.Scene["assets"]!["old-id"] = new JsonObject { ["path"] = "assets/original.png", ["mime"] = "image/png", ["sourceHash"] = new string('a', 64), ["dataUrl"] = "data:image/png;base64," + PngBase64 };
    document.Originals["source/export.html"] = "<div id='canvasContent'>synthetic mismatch</div>"u8.ToArray();
    var path = Path.Combine(work, "hash-mismatch.wbarchive"); await ArchiveService.SaveAsync(document, path);
    foreach (JsonNode hash in new JsonNode[] { JsonValue.Create(new string('b', 64))!, JsonValue.Create(17)!, new JsonObject() })
    {
        var scene = NewScene();
        scene["assets"]!["new-id"] = new JsonObject { ["path"] = "assets/new.png", ["mime"] = "image/png", ["sourceHash"] = hash.DeepClone() };
        scene["elements"] = new JsonArray(new JsonObject { ["id"] = "image", ["kind"] = "image", ["assetId"] = "new-id" });
        var result = await new ImportService(new FixtureNormalizer { NextScene = scene }).ImportAsync(path);
        Assert(result.Status == "partial" && result.Scene["diagnostics"]!["missingAssets"]!.AsArray().Any(item => item!.GetValue<string>() == "new-id"), "错误来源被按图片序号绑定或哈希类型导致重解析失败");
    }
}));
tests.Add(("部分备份补原生标识时保留已修复元素、媒体、分组、边界和来源哈希", async () =>
{
    var document = new BoardDocument { Scene = NewScene() }; AddEmbeddedImage(document.Scene); AssetService.ExtractEmbeddedAssets(document);
    var image = (JsonObject)document.Scene["elements"]![0]!;
    image["nativeType"] = "FluidImage"; image["html"] = "<div>已有修复内容</div>";
    document.Scene["assets"]!["image"]!["sourceHash"] = new string('a', 64);
    var child = new JsonObject { ["id"] = "child", ["kind"] = "unknown", ["nativeType"] = "FutureShape", ["text"] = "保留未知对象" };
    document.Scene["elements"]!.AsArray().Add(new JsonObject { ["id"] = "group", ["kind"] = "group", ["nativeType"] = "Group", ["nativeId"] = "existing-group", ["children"] = new JsonArray(child) });
    document.Scene["diagnostics"]!["status"] = "partial"; document.Scene["diagnostics"]!["unsupported"] = 1;
    document.Originals["source/export.html"] = "<div id='canvasContent'>partial-source</div>"u8.ToArray();
    var expectedElements = (JsonArray)document.Scene["elements"]!.DeepClone();
    expectedElements[0]!["nativeId"] = "native-image"; expectedElements[1]!["children"]![0]!["nativeId"] = "native-child";
    var beforeAssets = document.Scene["assets"]!.ToJsonString(); var beforeBounds = document.Scene["bounds"]!.ToJsonString();
    var path = Path.Combine(work, "partial-metadata.wbarchive"); await ArchiveService.SaveAsync(document, path); var beforeBytes = await File.ReadAllBytesAsync(path);
    var scene = NewScene();
    scene["elements"] = new JsonArray(new JsonObject { ["id"] = "group", ["kind"] = "group", ["nativeType"] = "Group", ["nativeId"] = "different-group", ["children"] = new JsonArray(new JsonObject { ["id"] = "child", ["kind"] = "shape", ["nativeType"] = "FutureShape", ["nativeId"] = "native-child", ["text"] = "不能覆盖" }) },
        new JsonObject { ["id"] = "image-element", ["kind"] = "image", ["nativeType"] = "FluidImage", ["nativeId"] = "native-image", ["html"] = "<div>旧源可见内容</div>" });
    var result = await new ImportService(new FixtureNormalizer { NextScene = scene }).ImportAsync(path);
    Assert(JsonNode.DeepEquals(result.Scene["elements"], expectedElements), "补标识替换了现有内容、分组或顺序");
    Assert(result.Scene["assets"]!.ToJsonString() == beforeAssets && result.Scene["bounds"]!.ToJsonString() == beforeBounds && result.Assets.Values.Single().SequenceEqual(Convert.FromBase64String(PngBase64)), "元数据升级改写了来源哈希、边界或媒体");
    Assert(result.Status == "partial" && (await File.ReadAllBytesAsync(path)).SequenceEqual(beforeBytes), "元数据升级篡改了源文件或掩盖未知类型");
}));
tests.Add(("旧 AzureImage 仅升级同 ID 和类型占位并带真实加载诊断", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    var old = new JsonObject { ["id"] = "azure-placeholder", ["kind"] = "unknown", ["nativeType"] = "AzureImage", ["z"] = 17, ["transform"] = new JsonArray(1, 0, 0, 1, -80, 300), ["bounds"] = new JsonObject { ["x"] = -12, ["y"] = -22, ["width"] = 80, ["height"] = 50 }, ["style"] = new JsonObject { ["opacity"] = .5 }, ["sourceFragment"] = "<div>原始未知片段</div>" };
    var note = new JsonObject { ["id"] = "note", ["kind"] = "note", ["text"] = "不改已有文字" };
    document.Scene["elements"] = new JsonArray(old, note);
    document.Scene["diagnostics"]!["status"] = "partial"; document.Scene["diagnostics"]!["total"] = 2; document.Scene["diagnostics"]!["supported"] = 1; document.Scene["diagnostics"]!["unsupported"] = 1;
    document.Scene["diagnostics"]!["items"] = new JsonArray(new JsonObject { ["elementId"] = "azure-placeholder", ["code"] = "UNSUPPORTED_TYPE", ["severity"] = "warning" });
    document.Originals["source/export.html"] = "<div id='canvasContent'>azure-source</div>"u8.ToArray();
    var path = Path.Combine(work, "partial-azure.wbarchive"); await ArchiveService.SaveAsync(document, path);
    var scene = NewScene(); scene["elements"] = new JsonArray(new JsonObject { ["id"] = "azure-placeholder", ["kind"] = "image", ["nativeType"] = "AzureImage", ["nativeId"] = "native-azure", ["text"] = "图片尚未加载", ["html"] = "<div>安全加载占位</div>" }, new JsonObject { ["id"] = "note", ["kind"] = "note", ["text"] = "旧源中的不同文字" });
    scene["diagnostics"]!["items"] = new JsonArray(new JsonObject { ["elementId"] = "azure-placeholder", ["code"] = "IMAGE_NOT_READY", ["severity"] = "warning" });
    var result = await new ImportService(new FixtureNormalizer { NextScene = scene }).ImportAsync(path);
    var upgraded = result.Scene["elements"]![0]!;
    Assert(upgraded["kind"]!.GetValue<string>() == "image" && upgraded["nativeId"]!.GetValue<string>() == "native-azure", "Azure 占位未升级为可恢复图片");
    foreach (var field in new[] { "z", "transform", "bounds", "style", "sourceFragment" }) Assert(JsonNode.DeepEquals(upgraded[field], old[field]), "升级改写了原几何或源片段");
    Assert(JsonNode.DeepEquals(result.Scene["elements"]![1], note), "升级改写了无关文字");
    var diagnostics = result.Scene["diagnostics"]!;
    Assert(result.Status == "partial" && diagnostics["unsupported"]!.GetValue<int>() == 0 && diagnostics["supported"]!.GetValue<int>() == 2, "升级计数失真或加载骨架被报为完整");
    Assert(diagnostics["items"]!.AsArray().Any(item => item?["code"]?.GetValue<string>() == "IMAGE_NOT_READY") && !diagnostics["items"]!.AsArray().Any(item => item?["code"]?.GetValue<string>() == "UNSUPPORTED_TYPE"), "没有更新该元素的真实加载诊断");
}));
tests.Add(("旧 FluidImage 升级沿来源哈希复用已捕获资源并映射重编号引用", async () =>
{
    const string url = "blob:synthetic-captured-image";
    var sourceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)));
    var document = new BoardDocument { Scene = NewScene() };
    document.Scene["elements"] = new JsonArray(new JsonObject { ["id"] = "legacy-image", ["kind"] = "unknown", ["nativeType"] = "FluidImage", ["html"] = "<img src='wbasset:old-asset'/>" });
    document.Scene["assets"]!["old-asset"] = new JsonObject { ["path"] = "assets/old.png", ["mime"] = "image/png", ["sourceHash"] = sourceHash, ["dataUrl"] = "data:image/png;base64," + PngBase64 };
    document.Scene["diagnostics"]!["status"] = "partial"; document.Scene["diagnostics"]!["supported"] = 0; document.Scene["diagnostics"]!["unsupported"] = 1;
    document.Scene["diagnostics"]!["items"] = new JsonArray(new JsonObject { ["elementId"] = "legacy-image", ["code"] = "UNSUPPORTED_TYPE", ["severity"] = "warning" });
    document.Originals["source/export.html"] = "<div id='canvasContent'>fluid-source</div>"u8.ToArray();
    var path = Path.Combine(work, "partial-fluid.wbarchive"); await ArchiveService.SaveAsync(document, path);
    var scene = NewScene(); scene["elements"] = new JsonArray(new JsonObject { ["id"] = "legacy-image", ["kind"] = "image", ["nativeType"] = "FluidImage", ["nativeId"] = "native-fluid", ["assetId"] = "new-asset", ["html"] = "<img src='wbasset:new-asset'/>" });
    scene["assets"]!["new-asset"] = new JsonObject { ["path"] = "assets/new.png", ["mime"] = "image/png", ["source"] = url };
    var result = await new ImportService(new FixtureNormalizer { NextScene = scene }).ImportAsync(path);
    Assert(result.Status == "complete" && result.Scene["elements"]![0]!["assetId"]!.GetValue<string>() == "old-asset" && result.Scene["elements"]![0]!["html"]!.GetValue<string>().Contains("wbasset:old-asset"), "图片引用重新编号后未复用正确资源");
    Assert(result.Scene["assets"]!.AsObject().Count == 1 && result.Scene["assets"]!["old-asset"]!["sourceHash"]!.GetValue<string>() == sourceHash && result.Scene["assets"]!["old-asset"]!["source"] is null && result.Assets.Values.Single().SequenceEqual(Convert.FromBase64String(PngBase64)), "升级丢失或暴露已脱敏媒体");
}));
tests.Add(("完整场景不重解析，部分场景不按序号或不同类型错误绑定", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    document.Originals["source/export.html"] = "<div id='canvasContent'>source</div>"u8.ToArray();
    var path = Path.Combine(work, "complete-no-reparse.wbarchive"); await ArchiveService.SaveAsync(document, path);
    var parser = new FixtureNormalizer { OnNormalize = () => throw new InvalidDataException("must not parse complete") };
    Assert((await new ImportService(parser).ImportAsync(path)).Status == "complete" && parser.CallCount == 0, "完整 Scene 被旧源重新覆盖");
    document.Scene["elements"] = new JsonArray(new JsonObject { ["id"] = "element-1", ["kind"] = "image", ["nativeType"] = "AzureImage" }, new JsonObject { ["id"] = "stable", ["kind"] = "unknown", ["nativeType"] = "FutureImage" });
    document.Scene["diagnostics"]!["status"] = "partial"; document.Scene["diagnostics"]!["unsupported"] = 1;
    var partialPath = Path.Combine(work, "partial-no-position.wbarchive"); await ArchiveService.SaveAsync(document, partialPath);
    var scene = NewScene(); scene["elements"] = new JsonArray(new JsonObject { ["id"] = "element-1", ["kind"] = "image", ["nativeType"] = "AzureImage", ["nativeId"] = "not-proven-by-sequence" }, new JsonObject { ["id"] = "stable", ["kind"] = "image", ["nativeType"] = "AzureImage", ["nativeId"] = "wrong-native-type" });
    var result = await new ImportService(new FixtureNormalizer { NextScene = scene }).ImportAsync(partialPath);
    Assert(result.Status == "partial" && result.Scene["elements"]!.AsArray().All(item => item?["nativeId"] is null) && result.Scene["elements"]![1]!["kind"]!.GetValue<string>() == "unknown", "按匿名序号或不同类型绑定了图片");
}));
tests.Add(("旧未知图片重解析从原 ZIP 补齐已有缺失资源而非丢弃新恢复字节", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    document.Scene["elements"] = new JsonArray(new JsonObject { ["id"] = "legacy-local-image", ["kind"] = "unknown", ["nativeType"] = "FluidImage" });
    document.Scene["assets"]!["existing-missing-id"] = new JsonObject { ["path"] = "assets/not-found.png", ["source"] = "images/picture.png", ["mime"] = "image/png" };
    document.Scene["diagnostics"]!["status"] = "partial"; document.Scene["diagnostics"]!["supported"] = 0; document.Scene["diagnostics"]!["unsupported"] = 1;
    document.Scene["diagnostics"]!["items"] = new JsonArray(new JsonObject { ["elementId"] = "legacy-local-image", ["code"] = "UNSUPPORTED_TYPE", ["severity"] = "warning" });
    document.Originals["source/microsoft-export.zip"] = Zip(("index.html", "<div id='canvasContent'>local image</div>"u8.ToArray()), ("images/picture.png", Convert.FromBase64String(PngBase64)));
    var path = Path.Combine(work, "partial-local-resource.wbarchive"); await ArchiveService.SaveAsync(document, path);
    var scene = NewScene(); scene["elements"] = new JsonArray(new JsonObject { ["id"] = "legacy-local-image", ["kind"] = "image", ["nativeType"] = "FluidImage", ["nativeId"] = "native-local", ["assetId"] = "new-asset", ["html"] = "<img src='wbasset:new-asset'/>" });
    scene["assets"]!["new-asset"] = new JsonObject { ["path"] = "assets/picture.png", ["source"] = "images/picture.png", ["mime"] = "image/png" };
    var result = await new ImportService(new FixtureNormalizer { NextScene = scene }).ImportAsync(path);
    Assert(result.Status == "complete" && result.Scene["elements"]![0]!["assetId"]!.GetValue<string>() == "existing-missing-id" && result.Scene["assets"]!.AsObject().Count == 1 && result.Assets.Values.Single().SequenceEqual(Convert.FromBase64String(PngBase64)), "没有给已有缺失资源元数据补回原包中的图片");
}));
tests.Add(("匿名旧 AzureImage 从原片段提取真实身份而不按新序号绑定", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    document.Scene["elements"] = new JsonArray(new JsonObject { ["id"] = "element-8", ["kind"] = "unknown", ["nativeType"] = "AzureImage", ["sourceFragment"] = "<div data-whiteboard-type='AzureImage'><div data-identity-key='native-exact-test'/></div>" });
    document.Scene["diagnostics"]!["status"] = "partial"; document.Scene["diagnostics"]!["supported"] = 0; document.Scene["diagnostics"]!["unsupported"] = 1;
    document.Scene["diagnostics"]!["items"] = new JsonArray(new JsonObject { ["elementId"] = "element-8", ["code"] = "UNSUPPORTED_TYPE", ["severity"] = "warning" });
    document.Originals["source/export.html"] = "<div id='canvasContent'>full-native-source</div>"u8.ToArray();
    var path = Path.Combine(work, "partial-native-identity.wbarchive"); await ArchiveService.SaveAsync(document, path);
    var full = NewScene();
    full["elements"] = new JsonArray(new JsonObject { ["id"] = "element-8", ["kind"] = "image", ["nativeType"] = "AzureImage", ["nativeId"] = "wrong-old-sequence" }, new JsonObject { ["id"] = "element-3", ["kind"] = "image", ["nativeType"] = "AzureImage", ["nativeId"] = "native-exact-test", ["html"] = "<div>真实身份对应的加载卡</div>" });
    full["diagnostics"]!["items"] = new JsonArray(new JsonObject { ["elementId"] = "element-3", ["code"] = "IMAGE_NOT_READY", ["severity"] = "warning" });
    var fragment = NewScene(); fragment["elements"] = new JsonArray(new JsonObject { ["id"] = "element-1", ["kind"] = "image", ["nativeType"] = "AzureImage", ["nativeId"] = "native-exact-test" });
    var parser = new FixtureNormalizer { SceneFactory = html => html.Contains("full-native-source") ? full : fragment };
    var result = await new ImportService(parser).ImportAsync(path);
    Assert(parser.CallCount == 2 && result.Scene["elements"]![0]!["id"]!.GetValue<string>() == "element-8" && result.Scene["elements"]![0]!["nativeId"]!.GetValue<string>() == "native-exact-test", "错误使用了旧序号或改变了旧Scene ID");
    Assert(result.Status == "partial" && result.Scene["diagnostics"]!["items"]!.AsArray().Any(item => item?["elementId"]?.GetValue<string>() == "element-8" && item?["code"]?.GetValue<string>() == "IMAGE_NOT_READY"), "新诊断没有映射回保留的旧对象ID");
    fragment["elements"]!.AsArray().Add(new JsonObject { ["id"] = "element-2", ["kind"] = "image", ["nativeType"] = "AzureImage", ["nativeId"] = "ambiguous-other-identity" });
    var ambiguous = await new ImportService(parser).ImportAsync(path);
    Assert(ambiguous.Scene["elements"]![0]!["nativeId"] is null && ambiguous.Scene["elements"]![0]!["kind"]!.GetValue<string>() == "unknown", "有歧义的旧片段被错误绑定");
}));
tests.Add(("部分备份元数据升级失败保留旧场景，取消传播且输入不变", async () =>
{
    var document = new BoardDocument { Scene = NewScene() };
    document.Scene["elements"] = new JsonArray(new JsonObject { ["id"] = "legacy", ["kind"] = "unknown", ["nativeType"] = "AzureImage", ["text"] = "应保留的旧场景" });
    document.Scene["diagnostics"]!["status"] = "partial"; document.Scene["diagnostics"]!["unsupported"] = 1;
    document.Originals["source/export.html"] = "<div id='canvasContent'>partial source</div>"u8.ToArray();
    var path = Path.Combine(work, "partial-failed-cancel.wbarchive"); await ArchiveService.SaveAsync(document, path); var before = await File.ReadAllBytesAsync(path);
    var broken = NewScene(); broken["elements"] = new JsonArray(new JsonObject { ["id"] = "legacy", ["kind"] = "image", ["nativeType"] = "AzureImage", ["nativeId"] = 17 });
    var restored = await new ImportService(new FixtureNormalizer { NextScene = broken }).ImportAsync(path);
    Assert(restored.Status == "partial" && JsonNode.DeepEquals(restored.Scene["elements"], document.Scene["elements"]) && restored.Scene["diagnostics"]!["items"]!.AsArray().Any(item => item?["code"]?.GetValue<string>() == "ARCHIVE_METADATA_REFRESH_FAILED"), "失败升级覆盖了旧场景或没有可见诊断");
    using var cancellation = new CancellationTokenSource();
    var parser = new FixtureNormalizer { NextScene = NewScene(), OnNormalize = cancellation.Cancel };
    await Throws<OperationCanceledException>(() => new ImportService(parser).ImportAsync(path, cancellation.Token));
    Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(before), "取消元数据升级改写了源备份");
}));
tests.Add(("重解析仍失败或抛错时保留失败诊断与原始备份", async () =>
{
    var document = new BoardDocument { Scene = FailedScene() };
    var html = "<div id='canvasContent'>synthetic</div>"u8.ToArray(); document.Originals["source/export.html"] = html;
    var path = Path.Combine(work, "recover-still-failed.wbarchive"); await ArchiveService.SaveAsync(document, path);
    var result = await new ImportService(new FixtureNormalizer { NextScene = FailedScene() }).ImportAsync(path);
    Assert(result.Status == "failed" && result.Originals["source/export.html"].SequenceEqual(html), "失败重解析被标记成功或丢失源数据");
    var parser = new FixtureNormalizer { NextScene = NewScene(), OnNormalize = () => throw new InvalidDataException("synthetic parser error") };
    var exceptionResult = await new ImportService(parser).ImportAsync(path);
    Assert(exceptionResult.Status == "failed" && exceptionResult.Scene["diagnostics"]!["items"]!.AsArray().OfType<JsonObject>().Any(item => item["code"]?.GetValue<string>() == "ARCHIVE_REPARSE_FAILED"), "解析异常没有可见保留诊断");
}));
tests.Add(("旧失败备份的重解析取消传播且不改写文件", async () =>
{
    var document = new BoardDocument { Scene = FailedScene() };
    document.Originals["source/export.html"] = "<div id='canvasContent'></div>"u8.ToArray();
    var path = Path.Combine(work, "recover-cancel.wbarchive"); await ArchiveService.SaveAsync(document, path);
    var before = await File.ReadAllBytesAsync(path);
    using var cancellation = new CancellationTokenSource();
    var parser = new FixtureNormalizer { NextScene = NewScene(), OnNormalize = cancellation.Cancel };
    await Throws<OperationCanceledException>(() => new ImportService(parser).ImportAsync(path, cancellation.Token));
    Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(before), "取消恢复改写了原始备份");
}));
tests.Add(("备份哈希失败时禁止先调用重解析器", async () =>
{
    var document = new BoardDocument { Scene = FailedScene() };
    document.Originals["source/export.html"] = "<div id='canvasContent'></div>"u8.ToArray();
    var path = Path.Combine(work, "recover-integrity.wbarchive"); await ArchiveService.SaveAsync(document, path);
    using (var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
    using (var archive = new ZipArchive(file, ZipArchiveMode.Update))
    {
        archive.GetEntry("originals/source/export.html")!.Delete();
        using var stream = archive.CreateEntry("originals/source/export.html").Open(); stream.Write("<div>changed</div>"u8);
    }
    var parser = new FixtureNormalizer { NextScene = NewScene() };
    await Throws<InvalidDataException>(() => new ImportService(parser).ImportAsync(path));
    Assert(parser.CallCount == 0, "完整性验证之前调用了解析器");
}));

tests.Add(("导出目录兼容旧平铺布局、媒体白板和同账号多格式分组", async () =>
{
    var directory = Path.Combine(work, "library-legacy"); Directory.CreateDirectory(directory);
    const string id = "00000000-0000-0000-0000-000000000031";
    var stem = "旧版白板__" + id;
    var scene = NewScene("旧版白板"); scene["board"]!["id"] = id;
    var document = new BoardDocument { Scene = scene };
    await ArchiveService.SaveAsync(document, Path.Combine(directory, stem + ".wbarchive"));
    await ArchiveService.SaveJsonAsync(document, Path.Combine(directory, stem + ".json"));
    foreach (var extension in new[] { ".zip", ".pdf", ".png", ".jpg", ".svg", ".html" })
        await File.WriteAllTextAsync(Path.Combine(directory, stem + extension), "test output");
    var mediaStem = "仅图片与PDF__00000000-0000-0000-0000-000000000032";
    foreach (var extension in new[] { ".pdf", ".jpeg" }) await File.WriteAllTextAsync(Path.Combine(directory, mediaStem + extension), "test output");
    await File.WriteAllTextAsync(Path.Combine(directory, "export-state.json"), scene.ToJsonString());
    await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), scene.ToJsonString());
    await File.WriteAllTextAsync(Path.Combine(directory, "unrelated.json"), "{\"version\":1}");
    var scan = await ExportLibraryService.ScanAsync(directory);
    Assert(scan.Entries.Count == 2 && scan.Issues.Count == 0, "旧版目录格式被拆成重复条目或控制 JSON 被误导入");
    var entry = scan.Entries.Single(item => item.BoardIdHint == id);
    Assert(entry.TitleFallback == "旧版白板" && entry.SourceCandidates.Select(Path.GetExtension).SequenceEqual(new[] { ".wbarchive", ".json", ".zip", ".html" }), "源数据优先顺序或标题不正确");
    Assert(entry.PdfPath is not null && entry.ImagePaths.Count == 2 && entry.HtmlPath is not null && entry.SvgPath is not null && entry.MediaPaths.Count == 5, "导出格式没有关联到同一白板");
    var media = scan.Entries.Single(item => item != entry);
    Assert(media.SourceCandidates.Count == 0 && media.PdfPath is not null && media.ImagePaths.Count == 1, "没有原始数据的媒体白板被遗漏");
    Assert(scan.Entries.All(item => item.Account is null && item.AccountDirectory is null), "旧平铺布局被错误归属到另一个账号");
}));
tests.Add(("多个账号的相同白板 ID 独立索引且账号清单可往返", async () =>
{
    var directory = Path.Combine(work, "library-accounts");
    var firstAccount = ExportAccount.FromLabel("first@example.com");
    var secondAccount = ExportAccount.FromLabel("second@example.com");
    const string id = "00000000-0000-0000-0000-000000000041";
    foreach (var account in new[] { firstAccount, secondAccount })
    {
        var accountFolder = await AccountDirectoryService.ResolveAsync(directory, account);
        var boardFolder = Path.Combine(accountFolder, "Untitled__" + id); Directory.CreateDirectory(boardFolder);
        var scene = NewScene(account.DisplayName); scene["board"]!["id"] = id;
        await ArchiveService.SaveJsonAsync(new BoardDocument { Scene = scene }, Path.Combine(boardFolder, "scene.json"));
        await File.WriteAllTextAsync(Path.Combine(boardFolder, "Untitled__" + id + ".png"), "test image");
    }
    var scan = await ExportLibraryService.ScanAsync(directory);
    Assert(scan.Entries.Count == 2 && scan.Entries.Select(entry => entry.Key).Distinct().Count() == 2, "不同账号同 ID 白板被合并");
    Assert(scan.Entries.All(entry => entry.BoardIdHint == id && entry.SourceCandidates.Count == 1 && entry.ImagePaths.Count == 1), "单白板多格式没有在账号内正确分组");
    Assert(scan.Entries.Select(entry => entry.Account?.Key).Order().SequenceEqual(new[] { firstAccount.Key, secondAccount.Key }.Order()), "账号清单没有映射到白板条目");
    foreach (var entry in scan.Entries)
    {
        Assert(entry.AccountDirectory is not null && entry.AccountLabel == entry.Account!.DisplayName, "账号显示名称被当作文件夹猜测");
        var selectedAccount = await ExportLibraryService.ScanAsync(entry.AccountDirectory!);
        var selectedBoard = await ExportLibraryService.ScanAsync(entry.DirectoryPath);
        Assert(selectedAccount.Entries.Single().Key == entry.Key && selectedBoard.Entries.Single().Key == entry.Key, "同一实际目录从不同入口扫描产生不同身份");
    }
}));
tests.Add(("损坏备份保留候选并可用 JSON 回退且扫描不改写文件", async () =>
{
    var directory = Path.Combine(work, "library-fallback", "恢复__00000000-0000-0000-0000-000000000051"); Directory.CreateDirectory(directory);
    var archive = Path.Combine(directory, "恢复__00000000-0000-0000-0000-000000000051.wbarchive");
    await File.WriteAllTextAsync(archive, "corrupt zip");
    var scene = NewScene("可用 JSON"); scene["board"]!["id"] = "00000000-0000-0000-0000-000000000051";
    var json = Path.Combine(directory, "scene.json"); await ArchiveService.SaveJsonAsync(new BoardDocument { Scene = scene }, json);
    var beforeArchive = await File.ReadAllBytesAsync(archive); var beforeJson = await File.ReadAllBytesAsync(json);
    var scan = await ExportLibraryService.ScanAsync(directory);
    Assert(scan.Entries.Count == 1 && scan.Entries[0].SourceCandidates.SequenceEqual(new[] { archive, json }), "损坏备份被静默丢掉或阻断 JSON 候选");
    BoardDocument? loaded = null; var failures = 0;
    foreach (var candidate in scan.Entries[0].SourceCandidates)
    {
        try { loaded = await importer.ImportAsync(candidate); break; }
        catch (InvalidDataException) { failures++; }
    }
    Assert(failures == 1 && loaded?.Title == "可用 JSON", "损坏备份没有通过后续候选恢复");
    var afterArchive = await File.ReadAllBytesAsync(archive); var afterJson = await File.ReadAllBytesAsync(json);
    Assert(beforeArchive.SequenceEqual(afterArchive) && beforeJson.SequenceEqual(afterJson), "扫描或回退导入改写了原导出");
}));
tests.Add(("目录索引只保留当前输出且排除资源、缓存和历史目录", async () =>
{
    var root = Path.Combine(work, "library-filter"); Directory.CreateDirectory(root);
    var directory = Path.Combine(root, "原白板__00000000-0000-0000-0000-000000000061"); Directory.CreateDirectory(directory);
    var first = Path.Combine(directory, "原白板__00000000-0000-0000-0000-000000000061.png");
    var renamed = Path.Combine(directory, "重命名.previous-story__00000000-0000-0000-0000-000000000061.png");
    await File.WriteAllTextAsync(first, "old image"); await File.WriteAllTextAsync(renamed, "latest image");
    File.SetLastWriteTimeUtc(first, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
    File.SetLastWriteTimeUtc(renamed, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
    await File.WriteAllTextAsync(Path.Combine(directory, "原白板__00000000-0000-0000-0000-000000000061.previous-123456abcdef.wbarchive"), "historic archive");
    foreach (var excluded in new[] { "source", "assets", "profiles", ".history", "originals", "responses" })
    {
        var folder = Path.Combine(root, excluded); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "hidden__" + excluded + ".pdf"), "not a library board");
    }
    var scan = await ExportLibraryService.ScanAsync(root);
    Assert(scan.Entries.Count == 1 && scan.Entries[0].ImagePaths.Single() == renamed && scan.Entries[0].SourceCandidates.Count == 0, "旧修订、资源或缓存被误索引，或实际标题被错误排除");
}));
tests.Add(("目录扫描错误隔离、数量与深度限制及取消", async () =>
{
    var root = Path.Combine(work, "library-limits"); Directory.CreateDirectory(root);
    await File.WriteAllTextAsync(Path.Combine(root, "visible.pdf"), "media");
    await File.WriteAllTextAsync(Path.Combine(root, "locked__test.json"), "{}");
    using (var locked = new FileStream(Path.Combine(root, "locked__test.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var scan = await ExportLibraryService.ScanAsync(root);
        Assert(scan.Entries.Any(entry => entry.PdfPath is not null) && scan.Issues.Any(issue => issue.Code == "FileUnreadable"), "不可读文件中断了同目录其他白板发现");
    }
    var child = Path.Combine(root, "child"); Directory.CreateDirectory(child); await File.WriteAllTextAsync(Path.Combine(child, "hidden.pdf"), "media");
    var shallow = await ExportLibraryService.ScanAsync(root, options: new(MaxDepth: 0));
    Assert(shallow.Entries.All(entry => entry.DirectoryPath == root) && shallow.Issues.Any(issue => issue.Code == "ScanDepthLimit"), "扫描未限制深度");
    var bounded = await ExportLibraryService.ScanAsync(root, options: new(MaxFileSystemEntries: 1));
    Assert(bounded.Issues.Any(issue => issue.Code == "ScanEntryLimit"), "扫描没有数量界限");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws<OperationCanceledException>(() => ExportLibraryService.ScanAsync(root, canceled.Token));
    var missing = await ExportLibraryService.ScanAsync(Path.Combine(root, "missing"));
    Assert(missing.Entries.Count == 0 && missing.Issues.Single().Code == "DirectoryMissing" && !Directory.Exists(missing.RootDirectory), "不存在的目录被扫描创建");
}));
tests.Add(("目录索引跳过链接及链接根目录，保留链接外文件", async () =>
{
    var root = Path.Combine(work, "library-links"); Directory.CreateDirectory(root);
    var outside = Path.Combine(work, "library-outside"); Directory.CreateDirectory(outside);
    await File.WriteAllTextAsync(Path.Combine(outside, "private.pdf"), "outside selected root");
    var link = Path.Combine(root, "linked");
    try { Directory.CreateSymbolicLink(link, outside); }
    catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
    { Console.WriteLine("SKIP link fixture: system does not permit symlink creation."); return; }
    try
    {
        var scan = await ExportLibraryService.ScanAsync(root);
        var selectedLink = await ExportLibraryService.ScanAsync(link);
        Assert(scan.Entries.Count == 0 && scan.Issues.Any(issue => issue.Code == "ReparsePointSkipped"), "扫描跟随链接读取根外文件");
        Assert(selectedLink.Entries.Count == 0 && selectedLink.Issues.Any(issue => issue.Code == "ReparsePointSkipped"), "直接选择链接根目录绕过限制");
        await Throws<InvalidDataException>(() => AccountDirectoryService.ResolveAsync(link, ExportAccount.FromLabel("linked account")));
        Assert(!File.Exists(Path.Combine(outside, AccountDirectoryService.ManifestName)), "链接根目录拒绝前已写入账号清单");
    }
    finally { if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link); }
    Assert(File.Exists(Path.Combine(outside, "private.pdf")), "链接清理改写了外部文件");
}));
tests.Add(("账号身份规范化、隔离目录与显示名称变更复用", async () =>
{
    var first = ExportAccount.FromLabel("  Test@Example.com  ");
    var same = ExportAccount.FromLabel("test@example.COM");
    var other = ExportAccount.FromLabel("Test+alias@Example.com");
    Assert(first.Key == same.Key && first.DisplayName == "Test@Example.com" && first.Key != other.Key, "账号键没有稳定规范化或不同别名共用了目录");
    var root = Path.Combine(work, "account-directory");
    var firstFolder = await AccountDirectoryService.ResolveAsync(root, first);
    var secondFolder = await AccountDirectoryService.ResolveAsync(root, other);
    Assert(firstFolder != secondFolder && (await AccountDirectoryService.ReadAccountAsync(firstFolder))?.Key == first.Key, "不同账号未独立存储或清单无法恢复身份");
    var renamed = first with { DisplayName = "新的显示名称", Email = "public-name@example.com" };
    var renamedFolder = await AccountDirectoryService.ResolveAsync(root, renamed);
    Assert(renamedFolder == firstFolder && Directory.GetDirectories(root).Length == 2 && (await AccountDirectoryService.ReadAccountAsync(firstFolder))?.Email == renamed.Email, "同一稳定账号的名称变化创建了新目录或未更新清单");
    var document = new BoardDocument { Scene = NewScene() }; document.Scene["board"]!["account"] = renamed.ToJson();
    var archive = Path.Combine(work, "account-metadata.wbarchive"); await ArchiveService.SaveAsync(document, archive);
    var restored = await importer.ImportAsync(archive);
    Assert(ExportAccount.FromJson(restored.Scene["board"]?["account"])?.Key == renamed.Key, "备份场景丢失账号身份");
}));
tests.Add(("账号目录碰撞和损坏清单拒绝，取消不创建目录", async () =>
{
    var account = ExportAccount.FromLabel("same display");
    var root = Path.Combine(work, "account-collision");
    var folder = await AccountDirectoryService.ResolveAsync(root, account);
    var differentKey = account with { Key = account.Key[..16] + new string(account.Key[16] == 'a' ? 'b' : 'a', 48) };
    await Throws<InvalidDataException>(() => AccountDirectoryService.ResolveAsync(root, differentKey));
    Assert((await AccountDirectoryService.ReadAccountAsync(folder))?.Key == account.Key, "碰撞拒绝时覆盖了原账号清单");
    await File.WriteAllTextAsync(Path.Combine(folder, AccountDirectoryService.ManifestName), "broken json");
    await Throws<InvalidDataException>(() => AccountDirectoryService.ResolveAsync(root, account));
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    var canceledRoot = Path.Combine(work, "account-canceled");
    await Throws<OperationCanceledException>(() => AccountDirectoryService.ResolveAsync(canceledRoot, account, canceled.Token));
    Assert(!Directory.Exists(canceledRoot), "取消任务仍写入新的账号目录");
}));

try
{
    foreach (var test in tests)
    {
        try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
        catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
    }
}
finally
{
    // Delete only the verified, randomly named directory created by this test executable.
    var absoluteWork = Path.GetFullPath(work);
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (absoluteWork.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absoluteWork).StartsWith("WhiteboardExporter-CoreTests-", StringComparison.Ordinal)) Directory.Delete(absoluteWork, recursive: true);
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed.");
return failures == 0 ? 0 : 1;

static JsonObject NewScene(string title = "中文测试白板") => new()
{
    ["schemaVersion"] = 1, ["board"] = new JsonObject { ["id"] = "test-board", ["title"] = title },
    ["bounds"] = new JsonObject { ["x"] = -200, ["y"] = -100, ["width"] = 1000, ["height"] = 700 },
    ["background"] = "#ffffff", ["elements"] = new JsonArray(), ["assets"] = new JsonObject(),
    ["diagnostics"] = new JsonObject { ["status"] = "complete", ["total"] = 1, ["supported"] = 1, ["unsupported"] = 0, ["missingAssets"] = new JsonArray(), ["items"] = new JsonArray() }
};
static void AddEmbeddedImage(JsonObject scene)
{
    scene["assets"]!["image"] = new JsonObject { ["mime"] = "image/png", ["path"] = "assets/initial.png", ["dataUrl"] = "data:image/png;base64," + PngBase64 };
    scene["elements"] = new JsonArray(new JsonObject { ["id"] = "image-element", ["kind"] = "image", ["assetId"] = "image" });
}
static JsonObject FailedScene()
{
    var scene = NewScene();
    scene["diagnostics"]!["status"] = "failed";
    scene["diagnostics"]!["total"] = 0;
    scene["diagnostics"]!["supported"] = 0;
    scene["diagnostics"]!["items"] = new JsonArray(new JsonObject { ["code"] = "NO_WHITEBOARD_ANCHORS", ["severity"] = "error", ["message"] = "旧版未识别白板对象" });
    return scene;
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
{
    using var memory = new MemoryStream();
    using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        foreach (var entry in entries) { using var output = zip.CreateEntry(entry.Name, CompressionLevel.Optimal).Open(); output.Write(entry.Bytes); }
    return memory.ToArray();
}

file sealed class FixtureNormalizer : IBoardNormalizer
{
    public JsonObject? NextScene { get; set; }
    public string? LastHtml { get; private set; }
    public string? LastComments { get; private set; }
    public string? LastId { get; private set; }
    public string? LastTitle { get; private set; }
    public int CallCount { get; private set; }
    public Action? OnNormalize { get; init; }
    public Func<string, JsonObject>? SceneFactory { get; init; }
    public Task<JsonObject> NormalizeAsync(string html, string? commentsJson, string id, string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastHtml = html; LastComments = commentsJson; LastId = id; LastTitle = title; CallCount++;
        OnNormalize?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((JsonObject)(SceneFactory?.Invoke(html) ?? NextScene ?? throw new InvalidOperationException("Set fixture scene before normalization.")).DeepClone());
    }
}

file static class FixtureData
{
    public const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4GQAAAAASUVORK5CYII=";
}
