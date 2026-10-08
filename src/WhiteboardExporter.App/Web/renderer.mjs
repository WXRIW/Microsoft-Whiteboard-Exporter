import {normalize} from './normalize.mjs';
import {prepareOfflineSvg} from './offline-media.mjs';
import {sanitizeMarkup,safeDataUrl,sanitizeStyle} from './sanitize.mjs';
import {finite,validMatrix,validBounds,collectBounds,unionRects,exportDimensions,exportViewport,printLayout,xmlEscape} from './geometry.mjs';

const SVG_NS='http://www.w3.org/2000/svg',HTML_NS='http://www.w3.org/1999/xhtml';
const stage=document.getElementById('stage'), viewport=document.getElementById('viewport'), empty=document.getElementById('empty'), printRoot=document.getElementById('print-root');
let current=null,rootSvg=null,zoom=1,pan={x:0,y:0},renderReady=true,readyGeneration=0,mode='preview',metrics={width:800,height:600};
let cachedBounds=null,exportSvgCache=null,exportScene=null,exportTransparent=false;
let mediaPreview=false;

function assetData(assetId) {
  const asset=current?.assets?.[assetId];
  if(asset?.invalid) return placeholderData('图片内容无法验证');
  return asset?.dataUrl ? safeDataUrl(asset.dataUrl) || placeholderData(assetId) : placeholderData(assetId);
}
function placeholderData(label='图片资源缺失') {
  const markup=`<svg xmlns="${SVG_NS}" width="300" height="180" viewBox="0 0 300 180"><rect width="300" height="180" fill="#f3f5f8" stroke="#a8b4c5" stroke-width="2"/><path d="M28 132l44-45 35 28 47-58 59 75" fill="none" stroke="#a8b4c5" stroke-width="7"/><text x="150" y="160" text-anchor="middle" fill="#68768a" font-size="12" font-family="Segoe UI,Microsoft YaHei,sans-serif">${xmlEscape(`资源缺失 · ${label}`)}</text></svg>`;
  return 'data:image/svg+xml;charset=utf-8,'+encodeURIComponent(markup);
}
function materialize(markup,svg=false) {
  let clean=sanitizeMarkup(markup,svg);
  clean=clean.replace(/wbasset:([\w.-]+)/g,(_,id)=>assetData(id));
  return clean;
}
function appendFragment(parent,markup,svg=false) {
  if(!markup) return;
  if(svg) {
    const parsed=new DOMParser().parseFromString(`<svg xmlns="${SVG_NS}">${materialize(markup,true)}</svg>`,'image/svg+xml');
    for(const child of Array.from(parsed.documentElement.childNodes)) parent.appendChild(document.importNode(child,true));
  } else {
    const parsed=new DOMParser().parseFromString(materialize(markup),'text/html');
    for(const child of Array.from(parsed.body.childNodes)) parent.appendChild(document.importNode(child,true));
  }
}
function drawElement(element,parent) {
  const group=document.createElementNS(SVG_NS,'g'), matrix=validMatrix(element.transform), bounds=validBounds(element.bounds);
  group.setAttribute('transform',`matrix(${matrix.join(' ')})`);
  group.setAttribute('data-element-id',String(element.id||''));
  group.setAttribute('opacity',String(Math.max(0,Math.min(1,finite(element.style?.opacity,1)))));
  parent.appendChild(group);
  if(element.svg) appendFragment(group,element.svg,true);
  if(element.html) {
    const foreign=document.createElementNS(SVG_NS,'foreignObject');
    foreign.setAttribute('x',String(bounds.x)); foreign.setAttribute('y',String(bounds.y));
    foreign.setAttribute('width',String(bounds.width)); foreign.setAttribute('height',String(bounds.height));
    foreign.setAttribute('overflow','visible');
    const wrapper=document.createElementNS(HTML_NS,'div');
    wrapper.setAttribute('style',`position:relative;box-sizing:border-box;width:${bounds.width}px;height:${bounds.height}px;font-family:"Segoe UI","Microsoft YaHei",sans-serif;font-size:16px;line-height:normal;overflow:visible;`);
    const origin=document.createElementNS(HTML_NS,'div'); origin.setAttribute('style',`position:absolute;left:${-bounds.x}px;top:${-bounds.y}px;width:0;height:0;overflow:visible;`);
    appendFragment(origin,element.html); wrapper.appendChild(origin); foreign.appendChild(wrapper); group.appendChild(foreign);
  }
  if(!element.svg && !element.html && !element.children?.length) {
    if(element.kind==='image' || element.assetId) {
      const image=document.createElementNS(SVG_NS,'image');
      for(const key of ['x','y','width','height']) image.setAttribute(key,String(bounds[key]));
      image.setAttribute('href',assetData(element.assetId)); image.setAttribute('preserveAspectRatio','xMidYMid meet');group.appendChild(image);
    } else {
      const foreign=document.createElementNS(SVG_NS,'foreignObject');
      for(const key of ['x','y','width','height']) foreign.setAttribute(key,String(bounds[key]));
      const text=document.createElementNS(HTML_NS,'div');
      const style=sanitizeStyle(element.style?.css||'');
      text.setAttribute('style',`box-sizing:border-box;width:100%;height:100%;white-space:pre-wrap;overflow-wrap:break-word;font:20px/1.4 "Segoe UI","Microsoft YaHei",sans-serif;color:#334155;${element.kind==='unknown'?'border:1px dashed #d39848;background:#fff6df;padding:8px;':''}${style}`);
      text.textContent=String(element.text||''); foreign.appendChild(text);group.appendChild(foreign);
    }
  }
  for(const child of sorted(element.children)) drawElement(child,group);
}
function sorted(elements) {return [...(elements||[])].sort((a,b)=>finite(a.z)-finite(b.z));}
function makeSvg({width,height,transparent=false,viewBox=null}={}) {
  if(mediaPreview)throw new Error('此文件仅供媒体预览；请使用原始备份或 JSON 重新导出。');
  const bounds=sceneBounds(), svg=document.createElementNS(SVG_NS,'svg');
  const visible=viewBox || bounds;
  svg.setAttribute('xmlns',SVG_NS);svg.setAttribute('xmlns:xlink','http://www.w3.org/1999/xlink');
  svg.setAttribute('width',String(width ?? bounds.width));svg.setAttribute('height',String(height ?? bounds.height));
  svg.setAttribute('viewBox',`${visible.x} ${visible.y} ${visible.width} ${visible.height}`);
  svg.style.overflow='hidden';
  svg.setAttribute('preserveAspectRatio','none');svg.setAttribute('role','img');svg.setAttribute('aria-label',String(current?.board?.title||'白板'));
  if(!transparent) {
    const background=document.createElementNS(SVG_NS,'rect');
    for(const key of ['x','y','width','height']) background.setAttribute(key,String(bounds[key]));
    // Only color syntax is accepted. A JSON background cannot introduce an URL.
    const color=/^(?:#[\da-f]{3,8}|rgba?\([\d\s.,%]+\)|hsla?\([\d\s.,%]+\)|[a-z]+)$/i.test(current?.background||'') ? current.background : '#ffffff';
    background.setAttribute('fill',color);svg.appendChild(background);
  }
  // Bounds are metadata, not a reliable paint extent. Keep every object and
  // definition; Chromium clips wide strokes and overflowing text to viewBox.
  for(const element of sorted(current?.elements)) drawElement(element,svg);
  return svg;
}
function sceneBounds() {
  if(cachedBounds) return cachedBounds;
  // Include all transformed geometry even when a caller supplied stale bounds.
  const boxes=collectBounds(current?.elements||[]);
  if(current?.bounds) boxes.push(validBounds(current.bounds));
  return cachedBounds=boxes.length ? unionRects(boxes) : {x:0,y:0,width:800,height:600};
}
function waitForRender() {
  const generation=++readyGeneration;renderReady=false;
  const images=Array.from(document.querySelectorAll('#stage img,#print-root img'));
  const imageTasks=images.map(img=>img.complete ? Promise.resolve() : new Promise(resolve=>{img.addEventListener('load',resolve,{once:true});img.addEventListener('error',resolve,{once:true});setTimeout(resolve,5000);}));
  const svgImages=Array.from(document.querySelectorAll('#stage image,#print-root image')).map(image=>new Promise(resolve=>{
    const src=image.getAttribute('href') || image.getAttribute('xlink:href');
    if(!src) {resolve();return;}
    const probe=new Image();probe.onload=probe.onerror=resolve;probe.src=src;if(probe.complete)resolve();setTimeout(resolve,5000);
  }));
  const fonts=document.fonts?.ready || Promise.resolve();
  Promise.all([...imageTasks,...svgImages,fonts]).then(()=>{
    // WPF pauses animation frames on a hidden WebView (e.g. the image tab).
    // Resources must finish first; afterwards two paint frames or a bounded
    // timer can release the host. CDP/PDF subsequently forces its own layout.
    let settled=false;
    const markReady=()=>{
      if(settled)return;settled=true;clearTimeout(fallback);
      if(generation===readyGeneration)renderReady=true;
    };
    const fallback=setTimeout(markReady,150);
    requestAnimationFrame(()=>requestAnimationFrame(markReady));
  }).catch(()=>{if(generation===readyGeneration)renderReady=true;});
}
function clearModes() {
  mode='preview';document.body.classList.remove('exporting','printing');
  document.documentElement.style.removeProperty('background');stage.style.background='white';
  document.body.style.removeProperty('--export-width');document.body.style.removeProperty('--export-height');
  printRoot.replaceChildren();document.getElementById('print-settings')?.remove();
}
function load(scene) {
  if(!scene || scene.schemaVersion!==1 || !Array.isArray(scene.elements)) throw new Error('不支持的 scene.json：需要 schemaVersion 1 和 elements 数组。');
  mediaPreview=false;current=scene;cachedBounds=null;exportSvgCache=null;exportScene=null;clearModes();const bounds=sceneBounds();metrics={width:bounds.width,height:bounds.height};
  rootSvg=makeSvg();stage.replaceChildren(rootSvg);stage.style.width=`${bounds.width}px`;stage.style.height=`${bounds.height}px`;
  empty.style.display='none';fit();waitForRender();return {...metrics,elementCount:scene.elements.length};
}
function loadOfflineSvg(input) {
  const prepared=prepareOfflineSvg(input),bounds=prepared.bounds;
  mediaPreview=true;current={board:{title:prepared.title},bounds};cachedBounds=bounds;exportSvgCache=null;exportScene=null;clearModes();
  rootSvg=prepared.svg;metrics={width:bounds.width,height:bounds.height};stage.replaceChildren(rootSvg);
  stage.style.width=`${bounds.width}px`;stage.style.height=`${bounds.height}px`;empty.style.display='none';fit();waitForRender();
  return {...metrics,title:prepared.title,bounds,blockedResources:prepared.blockedResources,removedActiveContent:prepared.removedActiveContent,mediaOnly:true};
}
function updateView() {stage.style.transform=`translate(${pan.x}px,${pan.y}px) scale(${zoom})`;}
function fit() {
  if(!current) return zoom;
  if(mode!=='preview') {clearModes();rootSvg=makeSvg();stage.replaceChildren(rootSvg);const b=sceneBounds();stage.style.width=`${b.width}px`;stage.style.height=`${b.height}px`;}
  const b=sceneBounds(), width=Math.max(1,viewport.clientWidth),height=Math.max(1,viewport.clientHeight);
  zoom=Math.max(0.00001,Math.min((width-48)/b.width,(height-48)/b.height));
  pan={x:(width-b.width*zoom)/2,y:(height-b.height*zoom)/2};updateView();return zoom;
}
function setZoom(value) {
  const next=Math.max(0.00001,Math.min(32,finite(value,1))),center={x:viewport.clientWidth/2,y:viewport.clientHeight/2},ratio=next/zoom;
  pan={x:center.x-(center.x-pan.x)*ratio,y:center.y-(center.y-pan.y)*ratio};zoom=next;updateView();return zoom;
}
function getExportMetrics({scale=1}={}) {
  if(!current) throw new Error('请先载入白板。');
  if(mediaPreview)throw new Error('此文件仅供媒体预览；请使用原始备份或 JSON 重新导出。');
  const bounds=sceneBounds();return {...exportDimensions(bounds,scale),bounds};
}
function prepareExport({scale=1,transparent=false,tile=null}={}) {
  if(!current) throw new Error('请先载入白板。');
  if(mediaPreview)throw new Error('此文件仅供媒体预览；请使用原始备份或 JSON 重新导出。');
  const output=exportViewport(sceneBounds(),scale,tile);
  if(!exportSvgCache || exportScene!==current || exportTransparent!==transparent) {
    // Keep the same geometry, definitions, decoded images, and DOM objects for
    // every scanline band. Only the viewport changes, so large exports parse
    // each path once instead of hundreds of times. A new load invalidates it.
    exportSvgCache=mode==='preview' && !transparent ? rootSvg : makeSvg({...output,transparent});
    exportScene=current;exportTransparent=transparent;
  }
  clearModes();mode='export';document.body.classList.add('exporting');metrics=output;
  document.documentElement.style.background=transparent?'transparent':'white';
  document.body.style.setProperty('--export-width',`${metrics.width}px`);document.body.style.setProperty('--export-height',`${metrics.height}px`);
  rootSvg=exportSvgCache;
  rootSvg.setAttribute('width',String(output.width));rootSvg.setAttribute('height',String(output.height));
  rootSvg.setAttribute('viewBox',`${output.viewBox.x} ${output.viewBox.y} ${output.viewBox.width} ${output.viewBox.height}`);
  if(stage.firstElementChild!==rootSvg) stage.replaceChildren(rootSvg);
  stage.style.width=`${metrics.width}px`;stage.style.height=`${metrics.height}px`;stage.style.transform='none';stage.style.background=transparent?'transparent':'white';
  waitForRender();return {...metrics};
}
function preparePrint({layout='Canvas'}={}) {
  if(!current) throw new Error('请先载入白板。');
  if(mediaPreview)throw new Error('此文件仅供媒体预览；请使用原始备份或 JSON 重新导出。');
  clearModes();mode='print';document.body.classList.add('printing');
  const settings=printLayout(sceneBounds(),layout),bounds=sceneBounds();
  // A pathological infinite canvas must not allocate millions of DOM pages.
  if(settings.pageCount>1000) throw new Error(`分页数 ${settings.pageCount} 超过 1,000 页上限；请选择整板适配 A4/A3。`);
  const css=document.createElement('style');css.id='print-settings';css.textContent=`@page{size:${settings.width}px ${settings.height}px;margin:0}`;document.head.appendChild(css);
  // Clone one parsed scene: repeated URL attributes share browser strings and
  // decoded images. SVG <use> cannot carry foreignObject text in Chromium.
  const master=makeSvg({width:bounds.width*settings.scale,height:bounds.height*settings.scale});
  for(let row=0;row<settings.rows;row++) for(let column=0;column<settings.columns;column++) {
    const page=document.createElement('div');page.className='print-page';page.style.cssText=`width:${settings.width}px;height:${settings.height}px`;
    const content=document.createElement('div');content.className='print-content';content.style.cssText=`left:${settings.margin}px;top:${settings.margin}px;width:${settings.contentWidth}px;height:${settings.contentHeight}px`;
    const svg=master.cloneNode(true);
    if(layout==='A4Tiles') {svg.style.left=`${-column*settings.contentWidth}px`;svg.style.top=`${-row*settings.contentHeight}px`;}
    else {svg.style.left=`${(settings.contentWidth-bounds.width*settings.scale)/2}px`;svg.style.top=`${(settings.contentHeight-bounds.height*settings.scale)/2}px`;}
    content.appendChild(svg);page.appendChild(content);printRoot.appendChild(page);
  }
  metrics={...settings,layout};waitForRender();return {...metrics};
}
function exportSvg() {
  if(!current) throw new Error('请先载入白板。');
  return '<?xml version="1.0" encoding="UTF-8"?>\n'+new XMLSerializer().serializeToString(makeSvg());
}
function exportHtml() {
  if(!current) throw new Error('请先载入白板。');
  const svg=new XMLSerializer().serializeToString(makeSvg()),title=xmlEscape(current.board?.title||'白板');
  const support=current.diagnostics?.status==='complete'?'所有资源已嵌入':'已保存资源已嵌入，未完整还原的内容请查看诊断';
  return `<!doctype html>\n<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src 'none'; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'"><title>${title}</title><style>html,body{margin:0;min-height:100%;background:#e9edf3;color:#344256;font-family:Segoe UI,Microsoft YaHei,sans-serif}header{padding:14px 24px;background:white;border-bottom:1px solid #dce2ea}header h1{margin:0;font-size:18px;font-weight:600}header p{margin:4px 0 0;font-size:12px}main{padding:24px;overflow:auto}main>svg{display:block;width:100%;height:auto;box-shadow:0 4px 20px #27364a15}@media print{header{display:none}main{padding:0}main>svg{box-shadow:none;width:100%}}</style></head><body><header><h1>${title}</h1><p>离线白板 · ${support} · 使用浏览器缩放查看细节</p></header><main>${svg}</main></body></html>`;
}
viewport.addEventListener('wheel',event=>{
  if(!current || mode!=='preview')return;event.preventDefault();
  const r=viewport.getBoundingClientRect(),point={x:event.clientX-r.left,y:event.clientY-r.top},next=Math.max(0.00001,Math.min(32,zoom*Math.exp(-event.deltaY*0.0015))),ratio=next/zoom;
  pan={x:point.x-(point.x-pan.x)*ratio,y:point.y-(point.y-pan.y)*ratio};zoom=next;updateView();
},{passive:false});
let drag=null;
viewport.addEventListener('pointerdown',event=>{if(!current||mode!=='preview'||event.button>1)return;drag={x:event.clientX,y:event.clientY,pan:{...pan}};viewport.setPointerCapture(event.pointerId);viewport.classList.add('dragging');});
viewport.addEventListener('pointermove',event=>{if(!drag)return;pan={x:drag.pan.x+event.clientX-drag.x,y:drag.pan.y+event.clientY-drag.y};updateView();});
const endDrag=()=>{drag=null;viewport.classList.remove('dragging');};viewport.addEventListener('pointerup',endDrag);viewport.addEventListener('pointercancel',endDrag);viewport.addEventListener('dblclick',()=>fit());
new ResizeObserver(()=>{if(current && mode==='preview')updateView();}).observe(viewport);
window.wb={normalize,load,loadOfflineSvg,fit,setZoom,getExportMetrics,prepareExport,preparePrint,exportSvg,exportHtml,getZoom:()=>zoom,getMetrics:()=>({...metrics}),isReady:()=>renderReady,whenReady:()=>new Promise(resolve=>{const poll=()=>renderReady?resolve():setTimeout(poll,20);poll();}),getScene:()=>mediaPreview?null:current};
window.wbReady=true;
