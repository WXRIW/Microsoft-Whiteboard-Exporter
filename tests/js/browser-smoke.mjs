// Integration smoke runner. App runtime needs neither Node nor Playwright.
// Usage: node tests/js/browser-smoke.mjs [absolute-path-to-playwright/index.mjs]
import assert from 'node:assert/strict';
import http from 'node:http';
import {readFile,mkdtemp,writeFile} from 'node:fs/promises';
import {dirname,join,extname,resolve} from 'node:path';
import {tmpdir} from 'node:os';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {officialEmptyExport,azureImageExport} from './normalization-fixtures.mjs';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../src/WhiteboardExporter.App/Web');
const output=await mkdtemp(join(tmpdir(),'wb-renderer-smoke-'));
const {chromium}=await import(process.argv[2] ? pathToFileURL(resolve(process.argv[2])).href : 'playwright');
const server=http.createServer(async(req,res)=>{
  const name=decodeURIComponent(new URL(req.url,'http://localhost').pathname).replace(/^\//,'')||'index.html',path=resolve(root,name);
  if(!path.startsWith(root+'\\') && !path.startsWith(root+'/')) {res.writeHead(403);res.end();return;}
  try {res.setHeader('Content-Type',({'.html':'text/html','.mjs':'text/javascript','.css':'text/css'})[extname(path)]||'application/octet-stream');res.end(await readFile(path));}
  catch {res.writeHead(404);res.end();}
});
// Windows can allocate a low ephemeral port blocked by Chromium (e.g. 10080).
// Select a port above the browser's reserved-service range before navigation.
do {
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  if(server.address().port>10080)break;
  await new Promise(resolve=>server.close(resolve));
} while(true);
const browser=await chromium.launch({headless:true,channel:process.env.WB_BROWSER_CHANNEL||'msedge'});
const page=await browser.newPage({viewport:{width:1000,height:720},deviceScaleFactor:1});
const errors=[];page.on('pageerror',error=>errors.push(String(error)));
const png='iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=';
const html=`<!doctype html><html><head><style>
.anchor{position:absolute;width:0;height:0;transform-origin:0 0}.noteBackground{background:#b3e3fc;width:304px;height:265px}.stickyNote{padding:16px;width:304px;height:265px;font:24px/1.3 Segoe UI}.textbox{width:220px;max-width:220px;font-size:24px;padding:16px;white-space:pre-wrap}.inkHitTestOverlay{opacity:0}.imageComponent{width:120px;height:100px;transform:translate(-50%,-50%)}.imageComponent img{width:120px;height:100px}
</style><script>window.pwned=true</script></head><body><div id="canvasContent">
<div class="anchor" data-whiteboard-type="InkGroup" data-id="ink" style="left:-200px;top:-90px;transform:matrix(1,.1,.2,1,15,20)"><svg class="PenStroke" viewBox="0 0 100 100" width="100" height="100"><g class="inkStroke" transform="matrix(.0078125,0,0,.0078125,0,0)"><path d="M0 0 L12800 0 L12800 640 L0 640Z" fill="#0067c0"/><polyline class="inkHitTestOverlay" points="0,0 12800,0"/></g></svg></div>
<div class="anchor" data-whiteboard-type="Note" data-id="note" style="left:30px;top:50px;transform:rotate(15deg)"><div class="noteBackground"><div class="stickyNote"><div data-block="true"><span data-text="true">中文便签</span></div><div data-block="true"><span data-text="true">离线重绘</span></div></div></div></div>
<div class="anchor" data-whiteboard-type="PlainText" data-id="text" style="left:420px;top:70px"><div class="textbox plainText textBoxCore"><div data-block="true"><span data-text="true" style="font-weight:700;color:#c02646">文字和列表</span></div><ul><li>项目一</li><li>项目二</li></ul></div></div>
<div class="anchor" data-whiteboard-type="Shape" data-id="shape" style="left:100px;top:450px"><div style="position:absolute;left:-60px;top:-40px;width:120px;height:80px"><svg width="120" height="80" viewBox="-60 -40 120 80"><g fill="#a9e1bc" stroke="#15803d" stroke-width="3pt"><path d="M-60 -40 L60 -40 L60 40 L-60 40Z"/></g></svg><div style="position:absolute;top:25px;left:28px;color:#154f2a">形状</div></div></div>
<div class="anchor" data-whiteboard-type="FluidImage" data-id="image" style="left:430px;top:380px;transform:matrix(.8,.2,-.2,.8,0,0)"><div class="imageComponent"><img src="data:text/plain;base64,${png}" alt="嵌入图"></div></div>
<div class="anchor" data-whiteboard-type="FluidImage" data-id="missing" style="left:620px;top:450px"><div class="imageComponent"><img src="https://not-loaded.invalid/private.png"></div></div>
<div class="anchor" data-whiteboard-type="Group" data-id="group" style="left:0px;top:600px;transform:scale(.5)"><div class="anchor" data-whiteboard-type="PlainText" data-id="child" style="left:-40px;top:20px"><div class="textbox">嵌套分组</div></div></div>
<div class="anchor" data-whiteboard-type="UnknownWidget" data-id="unknown" style="left:340px;top:550px"><div style="width:230px;height:70px;background:#ffd6a5">未知对象仍有安全预览<img onerror="window.pwned=true" src="javascript:alert(1)"></div><iframe src="https://evil.invalid"></iframe><script>window.pwned=true</script></div>
<div class="anchor" data-whiteboard-type="CommentThread" data-id="comment" style="left:800px;top:50px"><span aria-label="Comment hint: thread-1">评论</span></div>
<div class="anchor" id="remoteInkPool"></div></div></body></html>`;
try {
  const cloudSource=await readFile(resolve(root,'../Cloud/CloudWhiteboardService.cs'),'utf8');
  const authHelpers=cloudSource.match(/private const string Helpers = """\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
  const authBody=cloudSource.match(/internal static readonly string InteractiveLogin = [^\n]+"""\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
  assert.ok(authHelpers&&authBody,'Test the actual Cloud interactive-auth detector');
  const authScript='(() => {'+authHelpers+authBody,authPage=await browser.newPage();
  for(const [markup,interactive] of [
    ['<div role="progressbar" style="width:200px;height:80px">Microsoft 正在加载</div>',false],
    ['<input id="i0116" type="email" style="width:200px;height:30px">',true],
    ['<div style="display:none"><input id="i0116" type="email"><input type="password"></div><div>静默登录</div>',false],
    ['<input id="i0118" type="password" style="width:200px;height:30px">',true],
    ['<button id="otherTile" style="width:150px;height:30px">选择账号</button>',true]
  ]) {
    await authPage.setContent(markup);const before=await authPage.content();
    assert.equal(await authPage.evaluate(script=>eval(script),authScript),interactive);
    assert.equal(await authPage.content(),before,'Checking login readiness neither types nor changes login-page content');
  }
  const imageHelpers=cloudSource.match(/private const string ImageReferences = """\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
  const identityBody=cloudSource.match(/internal static string ImageSourceByIdentity[^\n]+"""\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
  assert.ok(imageHelpers&&identityBody,'Test the actual Cloud native-image identity matcher');
  const identityScript='(() => {'+imageHelpers+'const id="native-image-1";'+identityBody;
  await authPage.route('**/*',route=>route.abort());
  for(const [markup,expected] of [
    ['<div id="canvasContent"><div data-identity-key="native-image-1"><img src="https://example.invalid/a.png"><img src="https://example.invalid/a.png"></div></div>','https://example.invalid/a.png'],
    ['<div id="canvasContent"><div data-identity-key="native-image-1"><img src="https://example.invalid/a.png"><img src="https://example.invalid/b.png"></div></div>',null],
    ['<div id="canvasContent"><div data-identity-key="different-id"><img src="https://example.invalid/a.png"></div></div>',null],
    ['<div id="canvasContent"><img data-identity-key="native-image-1" src="https://example.invalid/a.png"></div>','https://example.invalid/a.png'],
    ['<div id="canvasContent"></div><div data-identity-key="native-image-1"><img src="https://example.invalid/a.png"></div>',null]
  ]) {
    await authPage.setContent(markup);const before=await authPage.content();
    assert.equal(await authPage.evaluate(script=>eval(script),identityScript),expected,'Only one unambiguous image source on this board and exact native identity may be recovered');
    assert.equal(await authPage.content(),before,'Reading image identity never changes the live board');
  }
  await authPage.close();
  await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);await page.waitForFunction(()=>window.wbReady===true);
  const scene=await page.evaluate(({html})=>window.wb.normalize({html,id:'smoke-board',title:'中文白板',commentsJson:JSON.stringify({commentThreads:[{id:'thread-1',comments:[{author:{name:'测试作者',email:'private@example.invalid'},body:'评论内容',displayDate:'2026-10-08'}]}]})}),{html});
  assert.equal(scene.elements.length,9);assert.equal(scene.diagnostics.total,10);assert.equal(scene.diagnostics.unsupported,1);assert.equal(scene.diagnostics.status,'partial');
  assert.deepEqual(scene.elements.find(e=>e.id==='ink').transform,[1,.1,.2,1,-185,-70]);
  assert.match(scene.elements.find(e=>e.id==='ink').svg,/M0 0 L12800 0/);
  assert.equal(scene.elements.find(e=>e.id==='group').children[0].id,'child');
  assert.equal(scene.elements.find(e=>e.id==='note').text,'中文便签\n离线重绘');
  assert.equal(Object.values(scene.assets).find(a=>a.dataUrl)?.mime,'image/png');
  assert.ok(scene.bounds.x<0 && scene.bounds.height>600);
  assert.ok(!scene.elements.some(e=>e.html?.includes('onerror=') || e.html?.includes('<script') || e.html?.includes('<iframe')));
  assert.ok(!JSON.stringify(scene.elements.find(e=>e.id==='comment')).includes('private@example.invalid'));
  await page.evaluate(scene=>window.wb.load(scene),scene);await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(await page.evaluate(()=>window.pwned),undefined);
  await page.screenshot({path:join(output,'preview.png')});
  const beforeMeasure=await page.evaluate(()=>document.querySelector('#stage>svg').getAttribute('width'));
  const native=await page.evaluate(()=>window.wb.getExportMetrics());
  assert.equal(native.width,Math.ceil(scene.bounds.width));assert.equal(native.height,Math.ceil(scene.bounds.height));assert.equal(native.scale,1);
  assert.equal(await page.evaluate(()=>document.querySelector('#stage>svg').getAttribute('width')),beforeMeasure,'Sizing a native export does not allocate or resize output DOM');
  const nativeOutput=await page.evaluate(()=>window.wb.prepareExport());await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(nativeOutput.width,native.width);assert.equal(nativeOutput.fullHeight,native.height);assert.equal(nativeOutput.scale,1);
  assert.equal(nativeOutput.viewBox.width,native.width,'Fractional source bounds pad the final pixel instead of stretching');
  await page.evaluate(()=>{window.__smokeExportSvg=document.querySelector('#stage>svg');window.__smokeExportInk=document.querySelector('#stage [data-element-id="ink"]');});
  const session=await page.context().newCDPSession(page);
  const nativeShot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:native.width,height:native.height,scale:1}});
  await writeFile(join(output,'native.png'),Buffer.from(nativeShot.data,'base64'));
  const region={x:150,y:150,width:420,height:300},nativeTile=await page.evaluate(tile=>window.wb.prepareExport({tile}),region);await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(nativeTile.width,region.width);assert.equal(nativeTile.fullWidth,native.width);assert.deepEqual(nativeTile.tile,region);
  assert.equal(nativeTile.viewBox.x,native.bounds.x+region.x);
  assert.equal(await page.evaluate(()=>document.querySelector('#stage>svg')===window.__smokeExportSvg && document.querySelector('#stage [data-element-id="ink"]')===window.__smokeExportInk),true,'Repeated internal tiles reuse the existing SVG and geometry DOM');
  await page.evaluate(()=>{delete window.__smokeExportSvg;delete window.__smokeExportInk;});
  const regionShot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:region.width,height:region.height,scale:1}});
  const regionDiff=await page.evaluate(({full,tile,region})=>Promise.all([full,tile].map(data=>new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>resolve(image);image.onerror=reject;image.src='data:image/png;base64,'+data;}))).then(([fullImage,tileImage])=>{
    const canvas=document.createElement('canvas');canvas.width=region.width;canvas.height=region.height;const context=canvas.getContext('2d');context.drawImage(fullImage,-region.x,-region.y);const pixels=context.getImageData(0,0,canvas.width,canvas.height).data;context.clearRect(0,0,canvas.width,canvas.height);context.drawImage(tileImage,0,0);const tilePixels=context.getImageData(0,0,canvas.width,canvas.height).data;let maxDelta=0,changed=0;for(let i=0;i<pixels.length;i+=4){let different=false;for(let c=0;c<4;c++){const delta=Math.abs(pixels[i+c]-tilePixels[i+c]);maxDelta=Math.max(maxDelta,delta);different ||= delta>0;}if(different)changed++;}return {maxDelta,changed};
  }),{full:nativeShot.data,tile:regionShot.data,region});
  assert.ok(regionDiff.maxDelta<=1 && regionDiff.changed<=8,`Native local viewport matches its full-image source pixels: ${JSON.stringify(regionDiff)}`);
  await writeFile(join(output,'native-tile.png'),Buffer.from(regionShot.data,'base64'));
  const metrics=await page.evaluate(()=>window.wb.prepareExport({scale:2,transparent:false}));await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(metrics.requestedScale,2);assert.equal(metrics.scale,2);assert.equal(metrics.limited,false);assert.equal(metrics.width,Math.ceil(scene.bounds.width*2));assert.equal(metrics.height,Math.ceil(scene.bounds.height*2));
  const shot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:metrics.width,height:metrics.height,scale:1}});
  await writeFile(join(output,'export.png'),Buffer.from(shot.data,'base64'));
  await page.evaluate(()=>{window.wb.fit();window.wb.setZoom(2.5);});await page.setViewportSize({width:540,height:420});
  const metrics2=await page.evaluate(()=>window.wb.prepareExport({scale:2,transparent:false}));await page.waitForFunction(()=>window.wb.isReady());
  assert.deepEqual(metrics2,metrics);
  const shot2=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:metrics2.width,height:metrics2.height,scale:1}});
  assert.equal(shot2.data,shot.data,'Raster output must be identical after preview zoom and viewport change');
  await session.send('Emulation.setDefaultBackgroundColorOverride',{color:{r:0,g:0,b:0,a:0}});
  const transparentMetrics=await page.evaluate(()=>window.wb.prepareExport({scale:2,transparent:true}));await page.waitForFunction(()=>window.wb.isReady());
  const transparentShot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:transparentMetrics.width,height:transparentMetrics.height,scale:1}});
  const alpha=await page.evaluate(data=>new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>{const canvas=document.createElement('canvas');canvas.width=1;canvas.height=1;const context=canvas.getContext('2d');context.drawImage(image,0,0);resolve(context.getImageData(0,0,1,1).data[3]);};image.onerror=reject;image.src='data:image/png;base64,'+data;}),transparentShot.data);
  assert.equal(alpha,0,'Transparent PNG must have actual alpha outside board objects');
  await writeFile(join(output,'transparent.png'),Buffer.from(transparentShot.data,'base64'));
  await session.send('Emulation.setDefaultBackgroundColorOverride',{});
  const svg=await page.evaluate(()=>window.wb.exportSvg()),standalone=await page.evaluate(()=>window.wb.exportHtml());
  assert.ok(!svg.includes('wbasset:'));assert.match(svg,/data:image\/png/);assert.ok(!standalone.includes('src="https://'));
  assert.match(standalone,/未完整还原的内容请查看诊断/);assert.ok(!standalone.includes('所有资源已嵌入'),'A partial HTML export does not claim that missing images were saved');
  await writeFile(join(output,'export.svg'),svg);await writeFile(join(output,'export.html'),standalone);
  const canvasPdf=await page.evaluate(()=>window.wb.preparePrint());await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(canvasPdf.layout,'Canvas');assert.equal(canvasPdf.pageCount,1);assert.equal(canvasPdf.margin,0);assert.equal(canvasPdf.scale,1);
  await page.pdf({path:join(output,'native-canvas.pdf'),preferCSSPageSize:true,printBackground:true});
  const canvasPdfText=(await readFile(join(output,'native-canvas.pdf'))).toString('latin1'),mediaBox=canvasPdfText.match(/\/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]/);
  assert.ok(mediaBox);assert.ok(Math.abs(Number(mediaBox[1])-canvasPdf.width*.75)<.5);assert.ok(Math.abs(Number(mediaBox[2])-canvasPdf.height*.75)<.5);
  assert.equal((canvasPdfText.match(/\/Type\s*\/Page\b/g)||[]).length,1,'Canvas PDF physically contains one page');
  const pdf=await page.evaluate(()=>window.wb.preparePrint({layout:'A4Fit'}));await page.waitForFunction(()=>window.wb.isReady());assert.equal(pdf.pageCount,1);
  await page.pdf({path:join(output,'export.pdf'),preferCSSPageSize:true,printBackground:true});
  const tiles=await page.evaluate(()=>window.wb.preparePrint({layout:'A4Tiles'}));assert.ok(tiles.pageCount>=1);await page.waitForFunction(()=>window.wb.isReady());
  await page.pdf({path:join(output,'export-tiles.pdf'),preferCSSPageSize:true,printBackground:true});
  const tileShot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:Math.ceil(tiles.width),height:Math.ceil(tiles.height),scale:1}});await writeFile(join(output,'pdf-tile-first-page.png'),Buffer.from(tileShot.data,'base64'));
  const notePixel=await page.evaluate(data=>new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>{const canvas=document.createElement('canvas');canvas.width=canvas.height=1;const context=canvas.getContext('2d');context.drawImage(image,-390,-310);resolve(Array.from(context.getImageData(0,0,1,1).data));};image.onerror=reject;image.src='data:image/png;base64,'+data;}),tileShot.data);
  assert.deepEqual(notePixel,[179,227,252,255],'Tiled PDF page renders HTML note backgrounds, not only native SVG ink');
  assert.equal(await page.evaluate(()=>document.querySelectorAll('#print-root .print-page').length),tiles.pageCount);
  assert.ok(await page.evaluate(()=>document.querySelector('#print-root .print-page foreignObject')),'Tiled pages keep real foreignObject text nodes because Chromium use references omit them');
  const bad=await page.evaluate(()=>window.wb.normalize({html:'<html><body>ordinary page<script>window.pwned=true</script></body></html>'}));assert.equal(bad.diagnostics.status,'failed');
  const emptyScene=await page.evaluate(html=>window.wb.normalize({html,commentsJson:'{"commentThreads":[]}',id:'empty-import',title:'合成空画布'}),officialEmptyExport);
  assert.equal(emptyScene.diagnostics.status,'complete');assert.equal(emptyScene.diagnostics.total,0);assert.equal(emptyScene.elements.length,0);
  assert.equal(emptyScene.diagnostics.items[0].code,'EMPTY_CANVAS');assert.equal(emptyScene.diagnostics.items[0].severity,'info');
  assert.match(emptyScene.diagnostics.items[0].message,/不证明云端白板为空/);
  await page.evaluate(scene=>window.wb.load(scene),emptyScene);await page.waitForFunction(()=>window.wb.isReady());
  const emptyMetrics=await page.evaluate(()=>window.wb.prepareExport());await page.waitForFunction(()=>window.wb.isReady());
  assert.deepEqual([emptyMetrics.width,emptyMetrics.height],[800,600]);assert.equal(await page.evaluate(()=>document.querySelectorAll('#stage [data-element-id]').length),0);
  const emptyShot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:emptyMetrics.width,height:emptyMetrics.height,scale:1}});
  const blankPixels=await page.evaluate(data=>new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>{const canvas=document.createElement('canvas');canvas.width=image.width;canvas.height=image.height;const context=canvas.getContext('2d');context.drawImage(image,0,0);resolve(context.getImageData(0,0,canvas.width,canvas.height).data.every(value=>value===255));};image.onerror=reject;image.src='data:image/png;base64,'+data;}),emptyShot.data);
  assert.equal(blankPixels,true,'A recognized empty Microsoft export renders a blank white board, not an import error or metadata');
  await writeFile(join(output,'empty-board.png'),Buffer.from(emptyShot.data,'base64'));
  const emptyPrint=await page.evaluate(()=>window.wb.preparePrint());await page.waitForFunction(()=>window.wb.isReady());assert.equal(emptyPrint.pageCount,1);
  await page.pdf({path:join(output,'empty-board.pdf'),preferCSSPageSize:true,printBackground:true});
  const rejectedEmptyInputs=[
    '<html><body><div id="canvasContent"></div></body></html>',
    '<html><body><div id="canvasContent" data-blueprint-type="Canvas">metadata</div><img src="data:image/png;base64,'+png+'"></body></html>',
    officialEmptyExport.replace('class="contentOrigin isMacOriOS-false isClickThroughMode-false"','class="unrelated"'),
    officialEmptyExport.replace('id="galaxy-pen"','id="unrelated-definition"'),
    officialEmptyExport.replace('data-identity-key="synthetic-empty-board"',''),
    officialEmptyExport.replace('id="sharedDescribedBy"','id="unknown-helper"'),
    officialEmptyExport.replace('</g>','<path d="M0 0L50 50" stroke="red"/></g>'),
    officialEmptyExport.replace('id="galaxy-pen"/>','id="galaxy-pen"/><rect width="100" height="100"/>'),
    officialEmptyExport.replace('</div>','<div data-object-type="future">未知对象不能当空白忽略</div></div>'),
    officialEmptyExport.replace('</div>','<canvas width="400" height="300"></canvas></div>'),
    officialEmptyExport.replace('</body>','<img src="data:image/png;base64,'+png+'"></body>'),
    officialEmptyExport.replace('</body>','<script>window.pwned=true</script></body>')
  ];
  for(const rejected of rejectedEmptyInputs) {
    const invalid=await page.evaluate(html=>window.wb.normalize({html}),rejected);assert.equal(invalid.diagnostics.status,'failed');assert.ok(!invalid.diagnostics.items.some(d=>d.code==='EMPTY_CANVAS'));
  }
  const orphanComment=await page.evaluate(html=>window.wb.normalize({html,commentsJson:JSON.stringify({commentThreads:[{id:'synthetic-unplaced-comment',comments:[{body:'无坐标评论'}]}]})}),officialEmptyExport);
  assert.equal(orphanComment.diagnostics.status,'failed');assert.equal(orphanComment.unattachedComments.length,1,'Unplaced comment data stays available and does not silently turn into an empty board');
  const readyAzure=await page.evaluate(html=>window.wb.normalize({html}),azureImageExport({source:'data:image/png;base64,'+png}));
  assert.equal(readyAzure.diagnostics.status,'complete');assert.equal(readyAzure.elements[0].kind,'image');assert.ok(readyAzure.elements[0].assetId);assert.equal(Object.values(readyAzure.assets)[0].mime,'image/png');
  assert.equal(readyAzure.elements[0].nativeId,'synthetic-azure-identity');assert.equal(readyAzure.elements[0].id,'azure-image','Source identity does not rename stable scene IDs');
  const loadingAzure=await page.evaluate(html=>window.wb.normalize({html}),azureImageExport({loading:true}));
  assert.equal(loadingAzure.diagnostics.status,'partial');assert.equal(loadingAzure.diagnostics.unsupported,0);assert.equal(loadingAzure.diagnostics.supported,1);
  assert.ok(loadingAzure.diagnostics.items.some(d=>d.code==='IMAGE_NOT_READY'));assert.match(loadingAzure.elements[0].html,/微软图片尚未加载完成/);assert.ok(!loadingAzure.elements[0].html.includes('ms-Shimmer'));
  await page.evaluate(scene=>window.wb.load(scene),loadingAzure);await page.waitForFunction(()=>window.wb.isReady());await page.screenshot({path:join(output,'loading-image.png')});
  const missingAzure=await page.evaluate(html=>window.wb.normalize({html}),azureImageExport());assert.equal(missingAzure.diagnostics.status,'partial');assert.ok(missingAzure.diagnostics.items.some(d=>d.code==='IMAGE_CONTENT_MISSING'));
  const loadingFluid=await page.evaluate(html=>window.wb.normalize({html}),azureImageExport({loading:true,type:'FluidImage'}));assert.equal(loadingFluid.diagnostics.status,'partial');assert.ok(loadingFluid.diagnostics.items.some(d=>d.code==='IMAGE_NOT_READY'),'FluidImage loaders must not be reported complete');
  const globalDefs=await page.evaluate(()=>window.wb.normalize({html:`<html><body><svg><defs><linearGradient id="outside-gradient"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient></defs></svg><div id="canvasContent"><div class="anchor" data-whiteboard-type="InkGroup"><svg width="100" height="100" viewBox="0 0 100 100"><path d="M0 0H100V100H0Z" fill="url(#outside-gradient)"/></svg></div></div></body></html>`}));
  assert.equal(globalDefs.diagnostics.status,'complete');assert.match(globalDefs.elements[0].svg,/id="outside-gradient"/);
  const microsoftFixture=await readFile(resolve(root,'../../../samples/microsoft-export.html'),'utf8'),commentFixture=await readFile(resolve(root,'../../../samples/microsoft-export-comments.json'),'utf8');
  const fixtureScene=await page.evaluate(({html,commentsJson})=>window.wb.normalize({html,commentsJson,id:'fixture',title:'微软格式示例'}),{html:microsoftFixture,commentsJson:commentFixture});
  assert.equal(fixtureScene.diagnostics.status,'complete');assert.ok(fixtureScene.elements.some(e=>e.kind==='table'));assert.ok(fixtureScene.elements.find(e=>e.id==='marker-red').svg);
  await page.evaluate(scene=>window.wb.load(scene),fixtureScene);await page.waitForFunction(()=>window.wb.isReady());await page.screenshot({path:join(output,'microsoft-fixture.png')});
  const demoScene=JSON.parse(await readFile(resolve(root,'../../../samples/demo-scene.json'),'utf8'));
  for(const asset of Object.values(demoScene.assets)) asset.dataUrl=`data:${asset.mime};base64,${(await readFile(resolve(root,'../../../samples',asset.path))).toString('base64')}`;
  await page.evaluate(scene=>window.wb.load(scene),demoScene);await page.waitForFunction(()=>window.wb.isReady());await page.screenshot({path:join(output,'demo-scene.png')});
  // A hidden WPF WebView suspends rAF. Suppress frames to reproduce that state
  // deterministically while Playwright polls with timers rather than rAF.
  await page.evaluate(scene=>{window.__wbOriginalRaf=window.requestAnimationFrame;window.requestAnimationFrame=()=>0;window.wb.load(scene);},demoScene);
  assert.equal(await page.evaluate(()=>window.wb.isReady()),false,'Load must not report readiness before fonts and images finish');
  await page.waitForFunction(()=>window.wb.isReady(),null,{polling:20,timeout:2000});
  // Loading again resets readiness; an earlier generation cannot complete it.
  await page.evaluate(scene=>{window.wb.load(scene);window.wb.load(scene);},demoScene);
  assert.equal(await page.evaluate(()=>window.wb.isReady()),false,'A new render generation resets readiness');
  await page.waitForFunction(()=>window.wb.isReady(),null,{polling:20,timeout:2000});
  await page.evaluate(()=>{window.requestAnimationFrame=window.__wbOriginalRaf;delete window.__wbOriginalRaf;});
  const huge={schemaVersion:1,board:{id:'huge-native',title:'超大原尺寸分块'},bounds:{x:-120.25,y:-80.75,width:30000.25,height:800.25},background:'#ffffff',assets:{},elements:[
    {id:'left-marker',kind:'shape',transform:[1,0,0,1,-110,-70],bounds:{x:0,y:0,width:50,height:50},svg:'<rect width="50" height="50" fill="#ff0000"/>'},
    {id:'right-group',kind:'group',transform:[1,0,0,1,29800,-60],bounds:{x:0,y:0,width:20,height:20},children:[{id:'right-marker',kind:'shape',transform:[1,0,0,1,20,10],bounds:{x:0,y:0,width:50,height:50},svg:'<rect width="50" height="50" fill="#00ff00"/>'}]}]};
  await page.evaluate(scene=>window.wb.load(scene),huge);await page.waitForFunction(()=>window.wb.isReady());
  const hugeSize=await page.evaluate(()=>window.wb.getExportMetrics());assert.equal(hugeSize.width,20000);assert.equal(hugeSize.height,534);assert.equal(hugeSize.scale,20000/huge.bounds.width);assert.equal(hugeSize.requestedScale,1);assert.equal(hugeSize.limited,true);
  const doubleHugeSize=await page.evaluate(()=>window.wb.getExportMetrics({scale:2}));assert.equal(doubleHugeSize.width,20000);assert.equal(doubleHugeSize.scale,hugeSize.scale);assert.equal(doubleHugeSize.requestedScale,2);assert.equal(doubleHugeSize.limited,true);
  const lastRegion={x:16384,y:0,width:3616,height:534},hugeTile=await page.evaluate(tile=>window.wb.prepareExport({scale:1,tile}),lastRegion);await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(hugeTile.fullWidth,20000);assert.equal(hugeTile.width,3616);assert.equal(hugeTile.scale,hugeSize.scale);assert.equal(hugeTile.limited,true);
  assert.equal(await page.evaluate(()=>document.getElementById('stage').style.width),'3616px','Internal tile capture allocates only its local viewport');
  assert.equal(await page.evaluate(()=>!!document.querySelector('#stage [data-element-id="left-marker"]')),true,'Retain off-tile objects because metadata bounds do not guarantee exact paint extents or self-contained definitions');
  assert.equal(await page.evaluate(()=>!!document.querySelector('#stage [data-element-id="right-marker"]')),true,'A nested object survives even when parent bounds underestimate its extent');
  const hugeTileShot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:3616,height:534,scale:1}});await writeFile(join(output,'huge-scaled-last-tile.png'),Buffer.from(hugeTileShot.data,'base64'));
  const markerX=Math.floor((29820-huge.bounds.x+25)*hugeSize.scale-lastRegion.x),markerY=Math.floor((-50-huge.bounds.y+25)*hugeSize.scale);
  const markerPixel=await page.evaluate(({data,x,y})=>new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>{const canvas=document.createElement('canvas');canvas.width=canvas.height=1;const context=canvas.getContext('2d');context.drawImage(image,-x,-y);resolve(Array.from(context.getImageData(0,0,1,1).data));};image.onerror=reject;image.src='data:image/png;base64,'+data;}),{data:hugeTileShot.data,x:markerX,y:markerY});
  assert.deepEqual(markerPixel,[0,255,0,255],'Capped internal tile retains the marker after the same uniform scale on both axes');
  const hugePdf=await page.evaluate(()=>window.wb.preparePrint());await page.waitForFunction(()=>window.wb.isReady());assert.equal(hugePdf.pageCount,1);assert.equal(hugePdf.width,19200);assert.ok(hugePdf.scale<1);
  const svgSize=await page.evaluate(()=>{const svg=document.querySelector('#print-root .print-content>svg');return {width:Number(svg.getAttribute('width')),height:Number(svg.getAttribute('height'))};});
  assert.ok(Math.abs(svgSize.width-huge.bounds.width*hugePdf.scale)<1e-8);assert.ok(Math.abs(svgSize.height-huge.bounds.height*hugePdf.scale)<1e-8,'Oversized PDF uses one scale on both axes, paper rounding adds padding');
  await page.pdf({path:join(output,'huge-canvas.pdf'),preferCSSPageSize:true,printBackground:true});
  const hugePdfText=(await readFile(join(output,'huge-canvas.pdf'))).toString('latin1'),hugeMedia=hugePdfText.match(/\/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]/);assert.ok(hugeMedia);assert.ok(Number(hugeMedia[1])<=14400.5);assert.equal((hugePdfText.match(/\/Type\s*\/Page\b/g)||[]).length,1);
  const overflowScene={schemaVersion:1,board:{id:'tile-overflow',title:'越界描边和文字分块回归'},bounds:{x:0,y:0,width:600,height:240},background:'#ffffff',assets:{},elements:[
    {id:'outside-stroke',kind:'ink',transform:[1,0,0,1,70,0],bounds:{x:0,y:0,width:10,height:200},svg:'<path d="M0 50L0 150" fill="none" stroke="#ff0000" stroke-width="100" stroke-linecap="round"/>'},
    {id:'outside-text',kind:'text',transform:[1,0,0,1,0,5],bounds:{x:0,y:0,width:50,height:30},html:'<div style="width:50px;overflow:visible;white-space:nowrap;color:#0067c0;font:30px/1.2 Segoe UI,sans-serif">ABCDEFGHIJKLMN</div>'},
    {id:'outside-definitions',kind:'shape',transform:[1,0,0,1,550,0],bounds:{x:0,y:0,width:10,height:10},svg:'<defs><linearGradient id="tile-shared-gradient"><stop offset="0" stop-color="#ffff00"/><stop offset="1" stop-color="#00ff00"/></linearGradient></defs>'},
    {id:'inside-gradient',kind:'shape',transform:[1,0,0,1,150,130],bounds:{x:0,y:0,width:60,height:40},svg:'<rect width="60" height="40" fill="url(#tile-shared-gradient)"/>'}]};
  await page.evaluate(scene=>window.wb.load(scene),overflowScene);await page.waitForFunction(()=>window.wb.isReady());
  const overflowFull=await page.evaluate(()=>window.wb.prepareExport());await page.waitForFunction(()=>window.wb.isReady());
  const overflowFullShot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:overflowFull.width,height:overflowFull.height,scale:1}});
  const overflowRegion={x:100,y:0,width:200,height:200};await page.evaluate(tile=>window.wb.prepareExport({tile}),overflowRegion);await page.waitForFunction(()=>window.wb.isReady());
  const overflowTileShot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:200,height:200,scale:1}});
  const overflowDiff=await page.evaluate(({full,tile,region})=>Promise.all([full,tile].map(data=>new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>resolve(image);image.onerror=reject;image.src='data:image/png;base64,'+data;}))).then(([fullImage,tileImage])=>{
    const canvas=document.createElement('canvas');canvas.width=region.width;canvas.height=region.height;const context=canvas.getContext('2d');context.drawImage(fullImage,-region.x,-region.y);const fullPixels=context.getImageData(0,0,canvas.width,canvas.height).data;context.clearRect(0,0,canvas.width,canvas.height);context.drawImage(tileImage,0,0);const pixels=context.getImageData(0,0,canvas.width,canvas.height).data;let maxDelta=0,changed=0,red=0,blue=0,gradient=0;for(let i=0;i<pixels.length;i+=4){let different=false;for(let c=0;c<4;c++){const delta=Math.abs(fullPixels[i+c]-pixels[i+c]);maxDelta=Math.max(maxDelta,delta);different ||= delta>0;}if(different)changed++;if(pixels[i]>245&&pixels[i+1]<10&&pixels[i+2]<10)red++;if(pixels[i]<10&&pixels[i+1]>80&&pixels[i+1]<130&&pixels[i+2]>150&&pixels[i+2]<220)blue++;if(pixels[i+1]>240&&pixels[i+2]<10&&pixels[i]>10&&pixels[i]<240)gradient++;}return {maxDelta,changed,red,blue,gradient};
  }),{full:overflowFullShot.data,tile:overflowTileShot.data,region:overflowRegion});
  assert.ok(overflowDiff.maxDelta<=1 && overflowDiff.changed<=8,`Out-of-bounds paint and cross-object definitions match full-image crop: ${JSON.stringify(overflowDiff)}`);
  assert.ok(overflowDiff.red>100,'100px stroke outside declared bounds extends into tile');assert.ok(overflowDiff.blue>100,'Text painted outside foreignObject metadata bounds extends into tile');assert.ok(overflowDiff.gradient>100,'Gradient definition supplied by an off-tile object stays available');
  await writeFile(join(output,'overflow-full.png'),Buffer.from(overflowFullShot.data,'base64'));await writeFile(join(output,'overflow-tile.png'),Buffer.from(overflowTileShot.data,'base64'));
  // Equal titles and object counts cannot be used as board identity. Switching
  // between them must invalidate preview, raster cache, and printable geometry.
  const identityBoard=(id,color)=>({schemaVersion:1,board:{id,title:'Untitled'},bounds:{x:0,y:0,width:320,height:180},background:'#ffffff',assets:{},diagnostics:{status:'complete'},elements:[
    {id:'same-local-element-id',kind:'shape',transform:[1,0,0,1,50,40],bounds:{x:0,y:0,width:90,height:70},svg:`<rect width="90" height="70" fill="${color}"/>`}
  ]});
  const boardA=identityBoard('synthetic-board-a','#ff0000'),boardB=identityBoard('synthetic-board-b','#00ff00');
  await page.evaluate(scene=>window.wb.load(scene),boardA);await page.waitForFunction(()=>window.wb.isReady());
  await page.evaluate(()=>window.wb.prepareExport());await page.waitForFunction(()=>window.wb.isReady());
  await page.evaluate(scenes=>{window.wb.load(scenes[1]);window.wb.load(scenes[0]);window.wb.load(scenes[1]);},[boardA,boardB]);
  await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(await page.evaluate(()=>window.wb.getScene().board.id),'synthetic-board-b');
  assert.equal(await page.evaluate(()=>document.querySelector('#stage [data-element-id="same-local-element-id"] rect').getAttribute('fill')),'#00ff00','Preview shows the last loaded board even with identical title, object count, dimensions, and local object ID');
  await page.evaluate(()=>window.wb.prepareExport());await page.waitForFunction(()=>window.wb.isReady());
  const identityShot=await session.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:320,height:180,scale:1}});
  const identityPixel=await page.evaluate(data=>new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>{const canvas=document.createElement('canvas');canvas.width=canvas.height=1;const context=canvas.getContext('2d');context.drawImage(image,-70,-60);resolve(Array.from(context.getImageData(0,0,1,1).data));};image.onerror=reject;image.src='data:image/png;base64,'+data;}),identityShot.data);
  assert.deepEqual(identityPixel,[0,255,0,255],'Raster output contains the current board instead of the previous export cache');
  await writeFile(join(output,'same-title-current-board.png'),Buffer.from(identityShot.data,'base64'));
  const identityPrint=await page.evaluate(()=>window.wb.preparePrint());await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(identityPrint.pageCount,1);
  assert.equal(await page.evaluate(()=>document.querySelector('#print-root [data-element-id="same-local-element-id"] rect').getAttribute('fill')),'#00ff00','PDF geometry uses the same current board as preview and raster');
  await page.pdf({path:join(output,'same-title-current-board.pdf'),preferCSSPageSize:true,printBackground:true});
  // Directory browsing may have only visual exports. Keep rich text from our
  // saved SVG/HTML, while never treating that visual as reconstructable data.
  await page.evaluate(scene=>window.wb.load(scene),fixtureScene);await page.waitForFunction(()=>window.wb.isReady());
  const mediaSvg=await page.evaluate(()=>window.wb.exportSvg()),mediaHtml=await page.evaluate(()=>window.wb.exportHtml());
  const mediaSourceViewBox=await page.evaluate(()=>document.querySelector('#stage>svg').getAttribute('viewBox'));
  const mediaSourceText=await page.evaluate(()=>document.querySelector('#stage>svg').textContent);
  const mediaSourceForeignCount=await page.evaluate(()=>document.querySelectorAll('#stage foreignObject').length);
  assert.ok(mediaSourceForeignCount>0,'Media roundtrip fixture must contain rich HTML');
  const mediaSourceShot=await page.screenshot();
  await writeFile(join(output,'media-source.png'),mediaSourceShot);
  const mediaMetrics=await page.evaluate(markup=>window.wb.loadOfflineSvg({markup,format:'svg',title:'offline'}),mediaSvg);await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(mediaMetrics.mediaOnly,true);assert.equal(mediaMetrics.blockedResources,0);
  assert.equal(await page.evaluate(()=>window.wb.getScene()),null,'A visual export never becomes an empty or complete SceneDocument');
  assert.equal(await page.evaluate(()=>document.querySelector('#stage>svg').getAttribute('viewBox')),mediaSourceViewBox);
  assert.equal(await page.evaluate(()=>document.querySelector('#stage>svg').textContent),mediaSourceText);
  assert.equal(await page.evaluate(()=>document.querySelectorAll('#stage foreignObject').length),mediaSourceForeignCount,'SVG rich text containers survive sanitized media preview');
  const mediaSvgShot=await page.screenshot();await writeFile(join(output,'media-svg.png'),mediaSvgShot);
  assert.ok(mediaSvgShot.equals(mediaSourceShot),`Saved SVG preview keeps actual rendered text, shape, image and placement pixels (${output})`);
  await page.evaluate(markup=>window.wb.loadOfflineSvg({markup,format:'html',title:'offline'}),mediaHtml);await page.waitForFunction(()=>window.wb.isReady());
  const mediaHtmlShot=await page.screenshot();await writeFile(join(output,'media-html.png'),mediaHtmlShot);
  assert.ok(mediaHtmlShot.equals(mediaSourceShot),`Self-contained HTML opens its board SVG without reinterpreting it as Microsoft source (${output})`);
  await assert.rejects(page.evaluate(()=>window.wb.preparePrint()),/仅供媒体预览/);
  await assert.rejects(page.evaluate(()=>window.wb.prepareExport()),/仅供媒体预览/);
  assert.equal(await page.evaluate(()=>document.body.classList.contains('printing')),false,'Rejected raw-data export does not hide the active media preview');
  const mediaRequests=[];page.on('request',request=>{if(request.url().startsWith('https://media-blocked.invalid'))mediaRequests.push(request.url());});
  const unsafeMedia=`<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180" viewBox="-120 -30 320 180"><style>#viewport{display:none}</style><rect x="-100" y="-10" width="70" height="50" fill="#ff0000"/><foreignObject x="0" y="0" width="160" height="130"><div xmlns="http://www.w3.org/1999/xhtml"><script>window.mediaPwned=true</script><iframe src="https://media-blocked.invalid/frame"></iframe><p onclick="window.mediaPwned=true">保留中文</p><img src="https://media-blocked.invalid/image.png" onerror="window.mediaPwned=true"/></div></foreignObject><image href="https://media-blocked.invalid/other.png" x="-80" y="60" width="50" height="50"/></svg>`;
  const unsafeMediaMetrics=await page.evaluate(markup=>window.wb.loadOfflineSvg({markup,format:'svg'}),unsafeMedia);await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(unsafeMediaMetrics.blockedResources,2);assert.ok(unsafeMediaMetrics.removedActiveContent>=4);
  assert.deepEqual(unsafeMediaMetrics.bounds,{x:-120,y:-30,width:320,height:180});
  assert.equal(await page.evaluate(()=>window.mediaPwned),undefined);assert.deepEqual(mediaRequests,[]);
  assert.equal(await page.evaluate(()=>!!document.querySelector('#stage script,#stage iframe,#stage style,#stage [onclick],#stage [onerror]')),false);
  assert.equal(await page.evaluate(()=>document.querySelector('#stage foreignObject p').textContent),'保留中文');
  await assert.rejects(page.evaluate(()=>window.wb.loadOfflineSvg({markup:'<svg xmlns="http://www.w3.org/2000/svg"><broken></svg>',format:'svg'})),/结构损坏/);
  await assert.rejects(page.evaluate(()=>window.wb.loadOfflineSvg({markup:'<!DOCTYPE svg [<!ENTITY unsafe "x">]><svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>',format:'svg'})),/DTD/);
  await assert.rejects(page.evaluate(()=>window.wb.loadOfflineSvg({markup:'<html><svg width="1" height="1"></svg><svg width="2" height="2"></svg></html>',format:'html'})),/唯一/);
  await page.evaluate(scene=>window.wb.load(scene),fixtureScene);await page.waitForFunction(()=>window.wb.isReady());
  assert.equal(await page.evaluate(()=>window.wb.getScene().board.id),fixtureScene.board.id,'Loading a real backup leaves media-only mode cleanly');
  assert.deepEqual(errors,[]);
  console.log(JSON.stringify({status:'passed',elements:scene.diagnostics.total,bounds:scene.bounds,raster:metrics,pdfPages:pdf.pageCount,output},null,2));
} finally {await browser.close();await new Promise(resolve=>server.close(resolve));}
