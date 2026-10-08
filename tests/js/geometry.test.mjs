import test from 'node:test';
import assert from 'node:assert/strict';
import {parseTransform,multiply,transformRect,collectBounds,unionRects,exportDimensions,exportViewport,printLayout,xmlEscape} from '../../src/WhiteboardExporter.App/Web/geometry.mjs';

test('anchor six-parameter matrices retain translation, skew and negative coordinates',()=>{
  assert.deepEqual(parseTransform('matrix(1, .2, -.3, 1, -120, 80)'),[1,.2,-.3,1,-120,80]);
  const rect=transformRect([0,1,-1,0,-10,20],{x:-20,y:-30,width:40,height:60});
  assert.deepEqual(rect,{x:-40,y:0,width:60,height:40});
});
test('nested group matrices compose parent and child without losing faraway objects',()=>{
  assert.deepEqual(multiply([2,0,0,2,-500,-600],[1,0,0,1,20,30]),[2,0,0,2,-460,-540]);
  const boxes=collectBounds([{transform:[2,0,0,2,-500,-600],bounds:{x:0,y:0,width:1,height:1},children:[{transform:[1,0,0,1,20000,30000],bounds:{x:0,y:0,width:50,height:50}}]}]);
  assert.equal(boxes[1].x,39500);assert.equal(boxes[1].y,59400);
  const union=unionRects(boxes,24);assert.equal(union.x,-524);assert.equal(union.width,40148);
});
test('transforms accept original CSS whitespace and rotation in radians',()=>{
  const matrix=parseTransform('translate(-8px, 11px) rotate(1.5707963267948966rad) scale(2)');
  assert.ok(Math.abs(matrix[0])<1e-10);assert.equal(matrix[1],2);assert.equal(matrix[2],-2);assert.equal(matrix[4],-8);assert.equal(matrix[5],11);
  assert.deepEqual(parseTransform('matrix3d(1,0,0,0,0,1,0,0,0,0,1,0,-45,32,0,1)'),[1,0,0,1,-45,32]);
});
test('percentage tiers uniformly scale both axes and retain the requested ratio',()=>{
  for(const scale of [.5,1,2,4]) {
    assert.deepEqual(exportDimensions({width:2400,height:1800},scale),{width:2400*scale,height:1800*scale,scale,requestedScale:scale,limited:false});
  }
  for(const invalid of [0,-1,NaN,Infinity,-Infinity]) assert.throws(()=>exportDimensions({width:100,height:50},invalid),RangeError);
});
test('default 100 percent and fractional bounds use padding instead of stretching',()=>{
  assert.deepEqual(exportDimensions({width:2400,height:1800}),{width:2400,height:1800,scale:1,requestedScale:1,limited:false});
  assert.deepEqual(exportDimensions({width:12.75,height:4.25}),{width:13,height:5,scale:1,requestedScale:1,limited:false});
  const output=exportViewport({x:-30.25,y:-5.5,width:12.75,height:4.25});
  assert.equal(output.scale,1);assert.deepEqual(output.viewBox,{x:-30.25,y:-5.5,width:13,height:5});
  const doubled=exportViewport({x:-30.25,y:-5.5,width:12.75,height:4.25},2);
  assert.equal(doubled.scale,2);assert.deepEqual(doubled.viewBox,{x:-30.25,y:-5.5,width:13,height:4.5});
});
test('either 20000 pixel bound uniformly caps the whole board with stable ceil rounding',()=>{
  assert.deepEqual(exportDimensions({width:50000,height:2}),{width:20000,height:1,scale:.4,requestedScale:1,limited:true});
  assert.deepEqual(exportDimensions({width:10000,height:40000},2),{width:5000,height:20000,scale:.5,requestedScale:2,limited:true});
  assert.deepEqual(exportDimensions({width:10000,height:10000},2),{width:20000,height:20000,scale:2,requestedScale:2,limited:false});
  for(const width of [20000.000000000004,19999.999999999996,30000.25,1e12]) {
    const output=exportDimensions({width,height:123.75},2);
    assert.equal(output.width,20000);assert.ok(output.height>=1 && output.height<=20000);assert.equal(output.scale,20000/width);assert.equal(output.requestedScale,2);assert.equal(output.limited,true);
    assert.ok(Math.abs(output.scale*width-20000)<1e-8);assert.ok(output.scale*123.75<=output.height,'Both axes share one scale; a fractional last pixel is padding');
  }
});
test('internal 100 percent tiles cover every output pixel and retain negative world origin',()=>{
  const bounds={x:-500.25,y:-200.75,width:10000.25,height:8000.75},full=exportDimensions(bounds);
  let covered=0,last;
  for(let y=0;y<full.height;y+=4096) for(let x=0;x<full.width;x+=4096) {
    const tile={x,y,width:Math.min(4096,full.width-x),height:Math.min(4096,full.height-y)},result=exportViewport(bounds,1,tile);
    assert.equal(result.fullWidth,10001);assert.equal(result.fullHeight,8001);assert.equal(result.scale,1);
    assert.equal(result.viewBox.x,bounds.x+x);assert.equal(result.viewBox.y,bounds.y+y);covered+=result.width*result.height;last=result;
  }
  assert.equal(covered,full.width*full.height);
  assert.equal(last.viewBox.x+last.viewBox.width,bounds.x+full.width);
  assert.equal(last.viewBox.y+last.viewBox.height,bounds.y+full.height);
  assert.throws(()=>exportViewport(bounds,1,{x:-1,y:0,width:10,height:10}),RangeError);
  assert.throws(()=>exportViewport(bounds,1,{x:10000,y:0,width:2,height:10}),RangeError);
});
test('percentage internal tiles retain a uniform scale and exact world coordinates',()=>{
  const b={x:-100,y:-40,width:300,height:100},size=exportDimensions(b,4),tile=exportViewport(b,4,{x:512,y:100,width:512,height:256});
  assert.ok(b.width*size.scale<=size.width);assert.ok(b.height*size.scale<=size.height);
  assert.equal(tile.scale,size.scale);assert.equal(tile.requestedScale,4);assert.equal(tile.viewBox.x,b.x+512/size.scale);assert.equal(tile.viewBox.width,512/size.scale);
  const cappedBounds={x:-500,y:-120,width:100000,height:90000},capped=exportViewport(cappedBounds,2,{x:16384,y:16384,width:3616,height:1616});
  assert.equal(capped.scale,.2);assert.equal(capped.limited,true);assert.equal(capped.fullWidth,20000);assert.equal(capped.fullHeight,18000);
  assert.equal(capped.viewBox.x,cappedBounds.x+16384/.2);assert.equal(capped.viewBox.y,cappedBounds.y+16384/.2);
  assert.equal(capped.viewBox.x+capped.viewBox.width,cappedBounds.x+cappedBounds.width);assert.equal(capped.viewBox.y+capped.viewBox.height,cappedBounds.y+cappedBounds.height);
});
test('default PDF is one dynamic canvas page with source scale and point-aligned paper',()=>{
  const layout=printLayout({width:2400,height:1800});
  assert.equal(layout.width,2400);assert.equal(layout.height,1800);assert.equal(layout.scale,1);assert.equal(layout.margin,0);assert.equal(layout.pageCount,1);
  const fractional=printLayout({width:100.25,height:70.125},'Canvas');
  assert.equal(fractional.scale,1);assert.ok(fractional.width>=100.25);assert.ok(fractional.height>=70.125);
  assert.equal(fractional.width*.75,Math.ceil(100.25*.75));assert.equal(fractional.height*.75,Math.ceil(70.125*.75));
});
test('oversized and narrow canvas PDFs use uniform compatibility scaling, never axis stretching',()=>{
  const wide=printLayout({width:20000,height:800},'Canvas');assert.equal(wide.scale,.96);assert.equal(wide.width,19200);assert.equal(wide.height,768);assert.equal(wide.pageCount,1);
  const narrow=printLayout({width:100000,height:1},'Canvas');assert.equal(narrow.scale,.192);assert.equal(narrow.width,19200);assert.equal(narrow.height,1/.75);assert.equal(narrow.margin,0);
  // The source SVG height remains 0.192px; extra paper is padding, not stretch.
  assert.ok(1*narrow.scale<narrow.height);
});
test('PDF fit covers the whole board and A4 tile cuts have no gaps',()=>{
  const fit=printLayout({width:30000,height:20000},'A4Fit');assert.equal(fit.pageCount,1);
  assert.ok(fit.scale*30000<=fit.contentWidth+1e-6);assert.ok(fit.scale*20000<=fit.contentHeight+1e-6);
  const tiled=printLayout({width:2500,height:1600},'A4Tiles');assert.equal(tiled.columns,3);assert.equal(tiled.rows,3);assert.equal(tiled.pageCount,9);assert.equal(tiled.scale,1);
  assert.ok(printLayout({width:800,height:600},'A3Fit').width>fit.width);
});
test('text escaping does not turn board titles into markup',()=>{assert.equal(xmlEscape('中文 <script> & "'), '中文 &lt;script&gt; &amp; &quot;');});
