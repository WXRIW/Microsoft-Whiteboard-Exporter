// Pure geometry shared by normalization, rendering, and Node smoke checks.
export const identity = () => [1, 0, 0, 1, 0, 0];
export const finite = (value, fallback = 0) => Number.isFinite(Number(value)) ? Number(value) : fallback;
export function multiply(a, b) {
  return [a[0]*b[0]+a[2]*b[1], a[1]*b[0]+a[3]*b[1],
    a[0]*b[2]+a[2]*b[3], a[1]*b[2]+a[3]*b[3],
    a[0]*b[4]+a[2]*b[5]+a[4], a[1]*b[4]+a[3]*b[5]+a[5]];
}
export function parseTransform(value) {
  if (!value || value === 'none') return identity();
  let result = identity(), matched = false;
  for (const match of String(value).matchAll(/([\w]+)\s*\(([^)]*)\)/g)) {
    const values = match[2].trim().split(/[\s,]+/).map(v => Number.parseFloat(v));
    if (values.some(v => !Number.isFinite(v))) continue;
    let part;
    switch (match[1].toLowerCase()) {
      case 'matrix': if (values.length === 6) part = values; break;
      case 'matrix3d': if (values.length === 16) part = [values[0],values[1],values[4],values[5],values[12],values[13]]; break;
      case 'translate': part = [1,0,0,1,values[0],values[1] || 0]; break;
      case 'translatex': part = [1,0,0,1,values[0],0]; break;
      case 'translatey': part = [1,0,0,1,0,values[0]]; break;
      case 'scale': part = [values[0],0,0,values[1] ?? values[0],0,0]; break;
      case 'scalex': part = [values[0],0,0,1,0,0]; break;
      case 'scaley': part = [1,0,0,values[0],0,0]; break;
      case 'rotate': {
        const angle = match[2].includes('rad') ? values[0] : values[0] * Math.PI / 180;
        part = [Math.cos(angle),Math.sin(angle),-Math.sin(angle),Math.cos(angle),0,0]; break;
      }
      case 'skewx': part = [1,0,Math.tan(values[0]*Math.PI/180),1,0,0]; break;
      case 'skewy': part = [1,Math.tan(values[0]*Math.PI/180),0,1,0,0]; break;
    }
    if (part) { result = multiply(result, part); matched = true; }
  }
  return matched ? result : identity();
}
export function transformPoint(matrix, x, y) {
  return {x:matrix[0]*x + matrix[2]*y + matrix[4], y:matrix[1]*x + matrix[3]*y + matrix[5]};
}
export function transformRect(matrix, bounds) {
  const {x,y,width,height} = validBounds(bounds);
  const points = [[x,y],[x+width,y],[x,y+height],[x+width,y+height]].map(([px,py]) => transformPoint(matrix,px,py));
  const xs = points.map(p=>p.x), ys=points.map(p=>p.y);
  return {x:Math.min(...xs),y:Math.min(...ys),width:Math.max(...xs)-Math.min(...xs),height:Math.max(...ys)-Math.min(...ys)};
}
export function validBounds(value) {
  return {x:finite(value?.x),y:finite(value?.y),width:Math.max(1,finite(value?.width,1)),height:Math.max(1,finite(value?.height,1))};
}
export function unionRects(rectangles, padding=0) {
  if (!rectangles.length) return {x:0,y:0,width:800,height:600};
  let minX=Infinity,minY=Infinity,maxX=-Infinity,maxY=-Infinity;
  for(const rect of rectangles) {minX=Math.min(minX,rect.x);minY=Math.min(minY,rect.y);maxX=Math.max(maxX,rect.x+rect.width);maxY=Math.max(maxY,rect.y+rect.height);}
  return {x:minX-padding,y:minY-padding,width:Math.max(1,maxX-minX+padding*2),height:Math.max(1,maxY-minY+padding*2)};
}
export function collectBounds(elements, parent=identity()) {
  return (elements || []).flatMap(element => {
    const matrix=multiply(parent, validMatrix(element.transform));
    return [transformRect(matrix,element.bounds),...collectBounds(element.children,matrix)];
  });
}
export function validMatrix(value) {
  return Array.isArray(value) && value.length===6 && value.every(v=>Number.isFinite(v)) ? value : identity();
}
export function exportDimensions(bounds,scale=1) {
  const b=validBounds(bounds),requestedScale=Number(scale);
  if(!Number.isFinite(requestedScale) || requestedScale<=0) throw new RangeError('图片缩放比例必须为有限的正数。');
  const appliedScale=Math.min(requestedScale,20000/b.width,20000/b.height);
  if(!Number.isFinite(1/appliedScale)) throw new RangeError('图片缩放比例过小，无法计算有限画布坐标。');
  // Ceil preserves a fractional edge as padding, never independent stretching.
  // Clamp the integer size as multiplication can round the exact cap up by an
  // IEEE-754 ulp. Both axes share the same applied scale, including tiny boards.
  const pixels=value=>Math.max(1,Math.min(20000,Math.ceil(value)));
  return {width:pixels(b.width*appliedScale),height:pixels(b.height*appliedScale),scale:appliedScale,requestedScale,limited:appliedScale<requestedScale};
}
export function exportViewport(bounds,scale=1,tile=null) {
  const b=validBounds(bounds),full=exportDimensions(b,scale);
  let offsetX=0,offsetY=0,width=full.width,height=full.height;
  if(tile) {
    for(const name of ['x','y','width','height']) {
      if(!Number.isSafeInteger(tile[name]) || tile[name]<(name==='x'||name==='y'?0:1)) throw new RangeError('分块坐标和尺寸必须为有效整数，尺寸须大于零。');
    }
    if(tile.x+tile.width>full.width || tile.y+tile.height>full.height) throw new RangeError('分块区域超出了完整画板输出范围。');
    offsetX=tile.x;offsetY=tile.y;width=tile.width;height=tile.height;
  }
  return {...full,width,height,fullWidth:full.width,fullHeight:full.height,offsetX,offsetY,bounds:b,tile:tile?{...tile}:null,
    viewBox:{x:b.x+offsetX/full.scale,y:b.y+offsetY/full.scale,width:width/full.scale,height:height/full.scale}};
}
export function printLayout(bounds,layout='Canvas') {
  // All page metrics are CSS px (96/in), not points. C# converts to inches.
  const b=validBounds(bounds);
  if(layout==='Canvas') {
    // 200in is our PDF reader compatibility policy, not an SDK width limit.
    // Paper is rounded up to PDF points; SVG still uses the single scale below.
    const scale=Math.min(1,19200/b.width,19200/b.height);
    const pointAligned=value=>Math.max(1,Math.ceil(Math.min(19200,value)*.75))/.75;
    const width=pointAligned(b.width*scale),height=pointAligned(b.height*scale);
    return {width,height,contentWidth:width,contentHeight:height,margin:0,scale,columns:1,rows:1,pageCount:1};
  }
  const size=layout==='A3Fit' ? [1587.401575,1122.519685] : [1122.519685,793.700787];
  const margin=30.23622, contentWidth=size[0]-margin*2, contentHeight=size[1]-margin*2;
  const tiled=layout==='A4Tiles';
  const scale=tiled ? 1 : Math.min(contentWidth/b.width,contentHeight/b.height);
  const columns=tiled ? Math.ceil(b.width/contentWidth) : 1, rows=tiled ? Math.ceil(b.height/contentHeight) : 1;
  return {width:size[0],height:size[1],contentWidth,contentHeight,margin,scale,columns,rows,pageCount:columns*rows};
}
export const xmlEscape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&apos;'}[c]));
