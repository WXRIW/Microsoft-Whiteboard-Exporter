import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

process.env.TZ='Asia/Shanghai';

// Exercise the gallery script shipped in WPF without credentials or live data.
// C# BoardMetadata tests separately verify timestamp validity and UTC conversion.
const source=readFileSync(new URL('../../src/WhiteboardExporter.App/Cloud/CloudWhiteboardService.cs',import.meta.url),'utf8');
function rawString(pattern,label) {
  const match=source.match(pattern);assert.ok(match,`Cannot extract ${label}`);
  const [,body,indent]=match;
  return body.split(/\r?\n/).map(line=>line.startsWith(indent)?line.slice(indent.length):line).join('\n');
}
const helpers=rawString(/private const string GalleryMetadata\s*=\s*"""\r?\n([\s\S]*?)\r?\n([ \t]*)""";/,'GalleryMetadata');
const body=rawString(/internal static readonly string ScanGallery\s*=\s*"\(\(\) => \{"\s*\+\s*GalleryMetadata\s*\+\s*"""\r?\n([\s\S]*?)\r?\n([ \t]*)""";/,'ScanGallery');
const script='(() => {'+helpers+body;

class Element {
  constructor(tag='div',attributes={},classes='') {
    this.tagName=tag;this.attributes={...attributes};this.id=attributes.id??'';
    this.classes=classes;this.children=[];this.childNodes=[];this.parentElement=null;
  }
  append(...nodes){for(const node of nodes){node.parentElement=this;this.children.push(node);this.childNodes.push(node);}return this;}
  text(textContent){this.childNodes.push({nodeType:3,textContent});return this;}
  get textContent(){return this.childNodes.map(node=>node.textContent??'').join('');}
  getAttribute(name){return this.attributes[name]??null;}
  matches(selector){return selector.split(',').some(part=>{
    const attributes=[...part.matchAll(/\[([\w-]+)(?:="([^"]*)")?\]/g)];
    const base=part.replace(/\[[^\]]*\]/g,'').trim(),tag=base.match(/^[\w-]+/);
    if(tag&&tag[0]!==this.tagName)return false;
    if([...base.matchAll(/\.([\w-]+)/g)].some(([,name])=>!this.classes.split(/\s+/).includes(name)))return false;
    return attributes.every(([,name,value])=>this.getAttribute(name)!==null&&(value===undefined||this.getAttribute(name)===value));
  });}
  closest(selector){for(let node=this;node;node=node.parentElement)if(node.matches(selector))return node;return null;}
  querySelectorAll(selector){const nodes=[];const visit=node=>{for(const child of node.children){if(child.matches(selector))nodes.push(child);visit(child);}};visit(this);return nodes;}
  querySelector(selector){return this.querySelectorAll(selector)[0]??null;}
}
const id='00000000-0000-4000-8000-000000000001';
function tile(attributes={},children=[]){
  const button=new Element('button',{id,...attributes},'boardPreviewOpenBoardButton');
  const caption=new Element('div',{},'captionText').text(' 中文白板 ');
  button.append(caption,...children);return button;
}
function scanLocale(locale,...boards){
  const grid=new Element('div',{},'scrollableGrid');
  Object.assign(grid,{scrollTop:0,clientHeight:600,scrollHeight:600});grid.append(...boards);
  const document={documentElement:{lang:locale},querySelector:selector=>grid.matches(selector)?grid:grid.querySelector(selector),querySelectorAll:selector=>grid.querySelectorAll(selector)};
  return JSON.parse(JSON.stringify(vm.runInNewContext(script,{document,navigator:{language:locale,languages:[locale]},location:{search:''},URLSearchParams},{timeout:1000})));
}
const scan=(...boards)=>scanLocale('en-US',...boards);

test('gallery preserves explicitly named machine timestamp and provenance',()=>{
  const result=scan(tile({'data-last-modified-time':' 2026-10-08T14:12:30+08:00 '}));
  assert.deepEqual(result.boards,[{id,title:'中文白板',modifiedText:'2026-10-08T14:12:30+08:00',modifiedSource:'microsoft-gallery:data-last-modified-time'}]);
  assert.equal(result.ready,true);assert.equal(result.bottom,true);assert.equal(result.loading,false);
});

test('descendant modified metadata is read without coercing to client time',()=>{
  const child=new Element('span',{'data-last-modified':'2026-10-07T23:15:00.1234567Z'});
  const board=scan(tile({},[child])).boards[0];
  assert.equal(board.modifiedText,'2026-10-07T23:15:00.1234567Z');
  assert.equal(board.modifiedSource,'microsoft-gallery:data-last-modified');
});

test('only time elements explicitly marked as modified or edited are read',()=>{
  for(const [attributes,classes] of [
    [{'datetime':'2026-10-08T01:02:03Z','data-timestamp-kind':'modified'},''],
    [{'datetime':'2026-10-08T01:02:03Z','data-date-kind':'edited'},''],
    [{'datetime':'2026-10-08T01:02:03Z'},'lastEditedTime'],
    [{'datetime':'2026-10-08T01:02:03Z','aria-label':'Last modified'},''],
    [{'datetime':'2026-10-08T01:02:03Z','aria-label':'最后编辑时间'},'']
  ]) {
    const board=scan(tile({},[new Element('time',attributes,classes)])).boards[0];
    assert.equal(board.modifiedText,'2026-10-08T01:02:03Z');
    assert.equal(board.modifiedSource,'microsoft-gallery:time[datetime]');
  }
});

test('parent metadata marks a time element as last modified',()=>{
  const parent=new Element('span',{'aria-label':'上次修改时间'}).append(new Element('time',{'datetime':'2026-10-08T01:02:03Z'}));
  const styled=new Element('span',{},'lastModifiedTime').append(new Element('time',{'datetime':'2026-10-08T01:02:03Z'}));
  assert.equal(scan(tile({},[parent])).boards[0].modifiedText,'2026-10-08T01:02:03Z');
  assert.equal(scan(tile({},[styled])).boards[0].modifiedText,'2026-10-08T01:02:03Z');
});

test('creation dates, generic time elements and relative captions remain unknown',()=>{
  const board=tile({'data-created-time':'2026-10-08T01:02:03Z'},[
    new Element('time',{'datetime':'2026-10-08T01:02:03Z','data-timestamp-kind':'created'}),
    new Element('time',{'datetime':'2026-10-08T01:02:03Z'}),
    new Element('span',{},'captionDate').text('Yesterday / 2 hours ago / 昨天')
  ]);
  const result=scan(board).boards[0];
  assert.equal(result.modifiedText,null);assert.equal(result.modifiedSource,null);
});

test('invalid board identity is excluded and unknown metadata is not copied',()=>{
  const valid=tile({'data-account-token':'never-copy','data-created-time':'never-copy'});
  const result=scan(valid,tile({id:'not-a-board-id'}));
  assert.equal(result.boards.length,1);
  assert.deepEqual(Object.keys(result.boards[0]).sort(),['id','modifiedSource','modifiedText','title']);
  assert.ok(!JSON.stringify(result).includes('never-copy'));
});

test('actual Edited caption is captured in browser timezone with minute precision',()=>{
  const board=scan(tile({},[new Element('span',{},'captionDate').text('Edited: 02/25/2020 1:47 PM')])).boards[0];
  assert.equal(board.modifiedText,'2020-02-25T05:47:00.000Z');
  assert.equal(board.modifiedSource,'microsoft-gallery:edited-caption;locale=en-US;timezone=Asia/Shanghai;precision=minute');
});

test('all four screenshot caption formats including ambiguous November date parse',()=>{
  for(const [caption,expected] of [
    ['Edited: 02/25/2020 1:47 PM','2020-02-25T05:47:00.000Z'],
    ['Edited: 03/22/2020 8:55 PM','2020-03-22T12:55:00.000Z'],
    ['Edited: 07/16/2021 10:29 PM','2021-07-16T14:29:00.000Z'],
    ['Edited: 11/07/2021 10:03 AM','2021-11-07T02:03:00.000Z']
  ])assert.equal(scan(tile({},[new Element('div',{},'boardPreviewSubText').text(caption)])).boards[0].modifiedText,expected);
});

test('localized day-month and Chinese caption clocks use declared locale',()=>{
  const uk=scanLocale('en-GB',tile({},[new Element('span',{},'boardPreviewSubText').text('Edited: 07/11/2021 10:03')])).boards[0];
  assert.equal(uk.modifiedText,'2021-11-07T02:03:00.000Z');
  const zh=scanLocale('zh-CN',tile({},[new Element('span',{},'boardPreviewSubText').text('最后编辑：2021年11月7日 下午10:03')])).boards[0];
  assert.equal(zh.modifiedText,'2021-11-07T14:03:00.000Z');
  const numeric=scanLocale('zh-CN',tile({},[new Element('span',{},'boardPreviewSubText').text('最后修改时间：2021/11/7 22:03:09')])).boards[0];
  assert.equal(numeric.modifiedText,'2021-11-07T14:03:09.000Z');
  assert.ok(numeric.modifiedSource.endsWith('precision=second'));
});

test('12 AM and PM, thin-space and direction marks parse without rollover',()=>{
  for(const [caption,expected] of [
    ['Edited: 10/08/2026 12:00 AM','2026-10-07T16:00:00.000Z'],
    ['\u200eEdited: 10/08/2026 12:00\u202fPM','2026-10-08T04:00:00.000Z']
  ])assert.equal(scan(tile({},[new Element('span',{},'boardPreviewSubText').text(caption)])).boards[0].modifiedText,expected);
});

test('explicit machine timestamp takes precedence over lower precision caption',()=>{
  const board=scan(tile({'data-last-modified-time':'2020-02-25T13:47:32+08:00'},[
    new Element('span',{},'boardPreviewSubText').text('Edited: 02/25/2020 1:47 PM')])).boards[0];
  assert.equal(board.modifiedText,'2020-02-25T13:47:32+08:00');
  assert.equal(board.modifiedSource,'microsoft-gallery:data-last-modified-time');
});

test('malformed machine fields still allow valid visible Edited caption',()=>{
  const board=scan(tile({'data-last-modified-time':'not-a-date'},[
    new Element('span',{},'boardPreviewSubText').text('Edited: 02/25/2020 1:47 PM')])).boards[0];
  assert.equal(board.modifiedText,'2020-02-25T05:47:00.000Z');
});

test('creation, viewed, relative, impossible dates and incomplete clocks stay unknown',()=>{
  for(const text of [
    'Created: 02/25/2020 1:47 PM','Viewed: 02/25/2020 1:47 PM',
    'Edited: Yesterday','Edited: 2 hours ago','Edited: 02/25/2020',
    'Edited: 02/30/2020 1:47 PM','Edited: 13/25/2020 1:47 PM',
    'Edited: 02/25/2020 0:47 PM','Edited: 02/25/2020 13:47 PM',
    'Edited: 02/25/2020 13:67','Edited: 02/25/20 13:47',
    'Untitled Edited: 02/25/2020 1:47 PM'
  ])assert.equal(scan(tile({},[new Element('span',{},'boardPreviewSubText').text(text)])).boards[0].modifiedText,null,text);
});

test('DST nonexistent and ambiguous local clocks remain unknown',()=>{
  process.env.TZ='America/New_York';
  try {
    for(const text of ['Edited: 03/08/2020 2:30 AM','Edited: 11/01/2020 1:30 AM'])
      assert.equal(scan(tile({},[new Element('span',{},'boardPreviewSubText').text(text)])).boards[0].modifiedText,null,text);
    assert.equal(scan(tile({},[new Element('span',{},'boardPreviewSubText').text('Edited: 11/01/2020 3:30 AM')])).boards[0].modifiedText,'2020-11-01T08:30:00.000Z');
  } finally {process.env.TZ='Asia/Shanghai';}
});

test('exact public board-card model resolves relative caption and excludes secrets',()=>{
  const board=tile({},[new Element('span',{},'boardPreviewSubText').text('Edited: Yesterday 1:47 PM')]);
  const root={memoizedProps:{board:{id,lastModifiedTime:'2026-10-07T05:47:23.4567890Z',createdTime:'2000-01-01T00:00:00Z',accessToken:'never-copy'}},return:null};root.stateNode={current:root};
  board['__reactFiber$fixture']={memoizedProps:{},return:root};
  const result=scan(board).boards[0];
  assert.equal(result.modifiedText,'2026-10-07T05:47:23.4567890Z');
  assert.equal(result.modifiedSource,'microsoft-gallery:board-model:lastModifiedTime');
  assert.ok(!JSON.stringify(result).includes('never-copy'));
});

test('neighbor board, uncommitted props, creation and viewed fields cannot supply edit date',()=>{
  for(const model of [
    {memoizedProps:{board:{id:'00000000-0000-4000-8000-000000000002',lastModifiedTime:'2026-10-07T05:47:23Z'}}},
    {pendingProps:{board:{id,lastModifiedTime:'2026-10-07T05:47:23Z'}}},
    {memoizedProps:{board:{id,createdTime:'2026-10-07T05:47:23Z',lastViewedTime:'2026-10-07T05:47:23Z'}}}
  ]){
    const board=tile();model.stateNode={current:model};board['__reactFiber$fixture']=model;
    assert.equal(scan(board).boards[0].modifiedText,null);
  }
});

test('cyclic component traversal is bounded and valid caption remains fallback',()=>{
  const board=tile({},[new Element('span',{},'boardPreviewSubText').text('Edited: 02/25/2020 1:47 PM')]);
  const fiber={memoizedProps:{}};fiber.return=fiber;board['__reactFiber$fixture']=fiber;
  assert.equal(scan(board).boards[0].modifiedText,'2020-02-25T05:47:00.000Z');
});

test('board title resembling an Edited caption cannot supply last edited date',()=>{
  const board=tile();board.children[0].childNodes[0].textContent='Edited: 02/25/2020 1:47 PM';
  assert.equal(scan(board).boards[0].modifiedText,null);
});

test('stale React branch resolves its committed alternate instead of old timestamp',()=>{
  const board=tile();
  const old={memoizedProps:{board:{id,lastModifiedTime:'2020-01-01T00:00:00Z'}}};
  const current={memoizedProps:{board:{id,lastModifiedTime:'2026-01-01T00:00:00Z'}}};
  const state={current};old.stateNode=current.stateNode=state;old.alternate=current;current.alternate=old;
  board['__reactFiber$fixture']=old;
  assert.equal(scan(board).boards[0].modifiedText,'2026-01-01T00:00:00Z');
});

test('model whose committed root cannot be established uses only visible date',()=>{
  const board=tile({},[new Element('span',{},'boardPreviewSubText').text('Edited: 02/25/2020 1:47 PM')]);
  board['__reactFiber$fixture']={memoizedProps:{board:{id,lastModifiedTime:'2026-01-01T00:00:00Z'}}};
  assert.equal(scan(board).boards[0].modifiedText,'2020-02-25T05:47:00.000Z');
});

test('impossible preferred ISO dates do not block a valid visible caption',()=>{
  for(const timestamp of ['2020-02-30T13:47:00Z','2021-02-29T13:47:00Z','2020-02-25T24:00:00Z','2020-02-25T13:47:00+15:00']){
    const board=tile({'data-last-modified-time':timestamp},[new Element('span',{},'boardPreviewSubText').text('Edited: 02/25/2020 1:47 PM')]);
    assert.equal(scan(board).boards[0].modifiedText,'2020-02-25T05:47:00.000Z',timestamp);
  }
});
