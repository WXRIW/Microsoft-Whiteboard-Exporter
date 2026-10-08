import {parseTransform,finite,identity,collectBounds,unionRects} from './geometry.mjs';
import {sanitizeNode,sanitizeStylesheet,sanitizeStyle,safeDataUrl,sniffMime,PRESENTATION_STYLES} from './sanitize.mjs';

const TYPE_MAP={inkgroup:'ink',ink:'ink',highlighter:'highlighter',fluidimage:'image',azureimage:'image',documentpage:'image',reactionstickers:'image',shape:'shape',note:'note',gridlist:'list',plaintext:'text',connector:'connector',hyperlink:'link',commentthread:'comment',group:'group',fluidgroup:'group',table:'table',gridtable:'table',list:'list',text:'text',image:'image'};
const NOTES={paleYellowGradient:'#FEE15A',softYellowGradient:'#FFF1A8',yellowGradient:'#FEE15A',softBlueGradient:'#C6E5FF',blueGradient:'#79C6FC',softGreenGradient:'#D5E9BD',greenGradient:'#B6D991',softPinkGradient:'#F9CFDB',pinkGradient:'#F7AEC4',softOrangeGradient:'#FFD0AD',orangeGradient:'#FABB89',purpleGradient:'#D7BCE8',softPurpleGradient:'#E8D9F0',grayGradient:'#C6C6C6',greyGradient:'#C6C6C6',whiteGradient:'#FFFFFF'};
const BASE_RULES=`.anchor{position:absolute;width:0;height:0;transform-origin:0 0}.center{transform:translate(-50%,-50%)}.textBoxCore{font-family:"Segoe UI","Microsoft YaHei",sans-serif;color:#242424}.textbox{padding:16px;white-space:pre-wrap;overflow-wrap:break-word}.plainText{width:max-content;max-width:400px;font-size:24px}.imageComponent{position:absolute;transform:translate(-50%,-50%)}.imageComponent img{width:100%;height:100%;object-fit:contain}.textBoxBackground{width:304px;min-height:305px;background:#FEE15A;overflow:hidden}.stickyNote{width:304px;min-height:265px;padding:16px;font-size:24px;white-space:pre-wrap}.authorBar{height:40px;padding:10px 16px;font-size:12px}.listChild{width:304px;margin:8px}.inkHitTestOverlay{display:none!important}.textBoxContainer{position:absolute;transform:translate(-50%,-50%);padding:16px}.source-root{position:relative;font-family:"Segoe UI","Microsoft YaHei",sans-serif;font-size:16px}svg{overflow:visible}${Object.entries(NOTES).map(([key,color])=>`.${key}{background-color:${color}}`).join('')}`;

/** Returns the independent v1 scene synchronously. No original HTML is navigated. */
export function normalize({html,commentsJson,id='local',title='未命名白板'}={}) {
  const parsed=new DOMParser().parseFromString(String(html||''),'text/html');
  const scene={schemaVersion:1,board:{id:String(id),title:String(title)},bounds:{x:0,y:0,width:800,height:600},background:'#ffffff',elements:[],assets:{},diagnostics:{status:'complete',total:0,supported:0,unsupported:0,missingAssets:[],items:[]}};
  const addDiagnostic=(elementId,code,message,severity='warning')=>scene.diagnostics.items.push({elementId,severity,code,message});
  let threads=[];
  try { const comments=typeof commentsJson==='string' ? JSON.parse(commentsJson||'{}') : commentsJson; threads=Array.isArray(comments?.commentThreads) ? comments.commentThreads : []; }
  catch { addDiagnostic('', 'COMMENTS_INVALID','评论 JSON 无法解析，原文件已保留。'); }
  const host=document.createElement('div');
  host.style.cssText='position:fixed;left:-100000px;top:0;width:1200px;height:900px;pointer-events:none;opacity:0;contain:layout style;';
  const shadow=host.attachShadow({mode:'closed'}), sourceRoot=document.createElement('div'); sourceRoot.className='source-root';
  const style=document.createElement('style'); style.textContent=BASE_RULES+'\n'+Array.from(parsed.querySelectorAll('style')).map(n=>sanitizeStylesheet(n.textContent)).join('\n');
  shadow.appendChild(style); shadow.appendChild(sourceRoot);
  for (const child of parsed.body.childNodes) { const clean=sanitizeNode(child,{preserveSources:true}); if(clean) sourceRoot.appendChild(clean); }
  document.body.appendChild(host);
  const canvas=sourceRoot.querySelector('#canvasContent') || sourceRoot.querySelector('[data-whiteboard-canvas]') || sourceRoot;
  if (canvas!==sourceRoot) canvas.style.cssText += ';position:relative;left:0;top:0;transform:none;';
  const candidates=Array.from(canvas.querySelectorAll('[data-whiteboard-type]'));
  const topAnchors=candidates.filter(node=>!closestTypedParent(node,canvas));
  let sequence=0,assetSequence=0;
  const assetsBySource=new Map();
  const newAsset=(url,elementId)=>{
    if (assetsBySource.has(url)) return assetsBySource.get(url);
    const assetId=`asset-${++assetSequence}`;
    const safe=safeDataUrl(url);
    const mime=safe ? safe.slice(5,safe.indexOf(';')) : inferMime(url);
    const extension={'image/png':'png','image/jpeg':'jpg','image/gif':'gif','image/webp':'webp','image/svg+xml':'svg','image/bmp':'bmp'}[mime] || 'bin';
    const asset={path:`assets/${assetId}.${extension}`,mime:mime||'application/octet-stream'};
    if (safe) asset.dataUrl=safe;
    else {
      asset.source=url;
      scene.diagnostics.missingAssets.push(assetId);
      addDiagnostic(elementId,/^data:/i.test(url)?'ASSET_UNSUPPORTED':'ASSET_UNAVAILABLE',/^data:/i.test(url) ? '嵌入资源不是可识别的 PNG、JPEG、GIF、WebP、BMP 或安全 SVG，保留原数据并显示占位图。' : /^https?:/i.test(url) ? '图片引用了远程地址；导入期间未联网取回。请补充本地资源后重绘。' : '图片资源未嵌入 HTML，正在从导出包查找；如果不存在，将显示占位图。');
    }
    assetsBySource.set(url,assetId); scene.assets[assetId]=asset; return assetId;
  };
  const parseAnchor=(anchor)=>{
    const nativeType=anchor.getAttribute('data-whiteboard-type') || 'Unknown',kind=TYPE_MAP[nativeType.toLowerCase()] || 'unknown';
    const elementId=anchor.getAttribute('data-whiteboard-id') || anchor.getAttribute('data-id') || anchor.id || `element-${++sequence}`;
    const commentHint=Array.from(anchor.querySelectorAll('[aria-label]')).map(n=>n.getAttribute('aria-label')).find(v=>/Comment hint:/i.test(v));
    if(kind==='comment' && !commentHint && !anchor.textContent.trim()) return null;
    scene.diagnostics.total++;
    if(kind==='unknown') { scene.diagnostics.unsupported++; addDiagnostic(elementId,'UNSUPPORTED_TYPE',`尚未支持微软对象类型 ${nativeType}，保留其源片段并显示安全可见内容。`); }
    else scene.diagnostics.supported++;
    const anchorStyle=getComputedStyle(anchor), originalMatrix=parseTransform(anchor.style.transform || anchorStyle.transform);
    const transform=[...originalMatrix]; transform[4]+=finite(parseFloat(anchor.style.left||anchorStyle.left)); transform[5]+=finite(parseFloat(anchor.style.top||anchorStyle.top));
    const element={id:elementId,kind,nativeType,z:scene.diagnostics.total-1,transform,bounds:{x:0,y:0,width:1,height:1},style:{opacity:finite(anchorStyle.opacity,1)},text:extractText(anchor)};
    const nativeId=anchor.getAttribute('data-identity-key') || anchor.querySelector('[data-identity-key]')?.getAttribute('data-identity-key');
    if(nativeId) element.nativeId=nativeId;
    const cloned=anchor.cloneNode(true);
    inlineComputed(anchor,cloned);
    for(const stylesheet of cloned.querySelectorAll('style')) stylesheet.remove();
    const nested=Array.from(anchor.querySelectorAll('[data-whiteboard-type]')).filter(node=>closestTypedParent(node,anchor)===anchor);
    if(nested.length) element.children=nested.map(parseAnchor).filter(Boolean);
    for(const child of Array.from(cloned.querySelectorAll('[data-whiteboard-type]'))) child.remove();
    cloned.removeAttribute('id'); cloned.style.cssText='position:relative;width:0;height:0;overflow:visible;transform:none;transform-origin:0 0;margin:0;padding:0;';
    const measure=document.createElement('div'); measure.style.cssText='position:absolute;left:0;top:0;width:0;height:0;'; measure.appendChild(cloned); shadow.appendChild(measure);
    const rects=[];
    const origin=measure.getBoundingClientRect();
    for(const child of cloned.querySelectorAll('*')) {
      const cs=getComputedStyle(child); if(cs.display==='none' || cs.visibility==='hidden' || cs.visibility==='collapse') continue;
      const r=child.getBoundingClientRect();
      if (r.width>0 && r.height>0 && Number.isFinite(r.width)) rects.push({x:r.left-origin.left,y:r.top-origin.top,width:r.width,height:r.height});
    }
    if(rects.length) element.bounds=unionRects(rects,kind==='ink'||kind==='highlighter'||kind==='connector'?2:0);
    else if(element.children?.length) element.bounds=unionRects(collectBounds(element.children));
    else element.bounds=fallbackBounds(anchor,kind);
    // AzureImage is another official image wrapper. A Shimmer is only a cloud
    // loading placeholder, not image content that can be faithfully backed up.
    const hasImageSource=Array.from(cloned.querySelectorAll('img,image')).some(image=>image.getAttribute('src')||image.getAttribute('href')||image.getAttribute('xlink:href')||image.getAttribute('data-wb-source'));
    if(['azureimage','fluidimage'].includes(nativeType.toLowerCase()) && !hasImageSource) {
      const loading=!!cloned.querySelector('.ms-Shimmer-container,.ms-Shimmer-shimmerWrapper,[aria-busy="true"]');
      addDiagnostic(elementId,loading?'IMAGE_NOT_READY':'IMAGE_CONTENT_MISSING',loading?'微软导出时图片尚未加载完成，仅保存了加载占位控件；请等待在线白板图片加载后重新导出。':'微软图片对象不包含图片内容；原始导出已保留，显示占位图。');
      const placeholder=document.createElement('div');
      placeholder.style.cssText=`position:absolute;left:${element.bounds.x}px;top:${element.bounds.y}px;width:${element.bounds.width}px;height:${element.bounds.height}px;box-sizing:border-box;display:flex;align-items:center;justify-content:center;overflow:hidden;border:2px dashed #a8b4c5;background:#f3f5f8;color:#68768a;padding:12px;font:16px/1.5 "Segoe UI","Microsoft YaHei",sans-serif;`;
      placeholder.textContent=loading?'微软图片尚未加载完成':'微软图片内容缺失';
      cloned.replaceChildren(placeholder);element.text=placeholder.textContent;
    }
    if(kind==='comment') {
      const threadId=commentHint?.replace(/^.*Comment hint:\s*/i,'').trim();
      const thread=threads.find(t=>String(t.id)===threadId);
      if(thread) {
        element.commentThreadId=threadId;
        element.comments=(thread.comments||[]).map(c=>({author:String(c.author?.name||'匿名'),body:String(c.body||''),displayDate:String(c.displayDate||'')}));
        const card=document.createElement('div'); card.style.cssText='background:#fff8dd;border:1px solid #e9cb60;border-radius:6px;padding:14px;width:280px;color:#333;font:14px/1.5 "Segoe UI","Microsoft YaHei",sans-serif;white-space:pre-wrap;';
        for(const comment of element.comments) {
          const row=document.createElement('div'), author=document.createElement('strong'); author.textContent=comment.author+(comment.displayDate?` · ${comment.displayDate}`:''); row.appendChild(author); row.appendChild(document.createElement('br')); row.appendChild(document.createTextNode(comment.body)); row.style.marginBottom='10px'; card.appendChild(row);
        }
        cloned.replaceChildren(card); element.bounds={x:0,y:0,width:280,height:Math.max(80,card.getBoundingClientRect().height)};
      } else { addDiagnostic(elementId,'COMMENT_NOT_FOUND','评论定位标记缺少对应的评论正文。'); }
    }
    // Some ink paints and connector markers reference definitions elsewhere in
    // the original export. Bring those definitions into the independent object.
    for(const localSvg of cloned.querySelectorAll('svg')) {
      const existing=new Set(Array.from(localSvg.querySelectorAll('[id]')).map(n=>n.id)),refs=new Set();
      for(const node of [localSvg,...localSvg.querySelectorAll('*')]) for(const attr of node.attributes) {
        for(const ref of attr.value.matchAll(/url\(\s*["']?#([\w:.-]+)/g)) refs.add(ref[1]);
        if((attr.name==='href'||attr.name==='xlink:href') && attr.value.startsWith('#')) refs.add(attr.value.slice(1));
      }
      for(const ref of refs) {
        if(existing.has(ref)) continue;
        const original=sourceRoot.querySelector(`#${CSS.escape(ref)}`);
        if(original && original.namespaceURI==='http://www.w3.org/2000/svg') {
          const definition=original.cloneNode(true);inlineComputed(original,definition);
          let defs=localSvg.querySelector('defs');if(!defs){defs=document.createElementNS('http://www.w3.org/2000/svg','defs');localSvg.prepend(defs);}
          defs.appendChild(definition);existing.add(ref);
        } else {
          addDiagnostic(elementId,'SVG_DEFINITION_UNAVAILABLE',`SVG 引用了缺少的定义 #${ref}；已使用可见占位颜色。`);
          for(const node of [localSvg,...localSvg.querySelectorAll('*')]) {
            for(const attr of Array.from(node.attributes)) if(attr.value.includes(`#${ref}`) && ['fill','stroke'].includes(attr.name)) node.setAttribute(attr.name,'#8b5cf6');
            for(const property of ['fill','stroke']) if(node.style.getPropertyValue(property).includes(`#${ref}`)) node.style.setProperty(property,'#8b5cf6');
          }
        }
      }
    }
    const images=Array.from(cloned.querySelectorAll('img,image'));
    for(const image of images) {
      const url=image.getAttribute('src') || image.getAttribute('href') || image.getAttribute('xlink:href') || image.getAttribute('data-wb-source');
      if(!url) continue;
      const assetId=newAsset(url,elementId); image.setAttribute('data-wb-asset',assetId); image.removeAttribute('data-wb-source');
      image.removeAttribute('src'); image.removeAttribute('href'); image.removeAttribute('xlink:href');
      image.setAttribute(image.localName.toLowerCase()==='img' ? 'src' : 'href',`wbasset:${assetId}`);
      if(kind==='image' && !element.assetId) element.assetId=assetId;
    }
    // Keep variable-width ink, head geometry, SVG transforms and paint exactly.
    // Pure SVG objects remain vectors; mixed objects use sanitized XHTML.
    const svg=cloned.querySelector('svg');
    const pureSvg=svg && ['ink','highlighter','connector'].includes(kind) && !element.text;
    if(kind==='shape' && svg) {
      const pieces=[];
      for(const shapeSvg of Array.from(cloned.querySelectorAll('svg')).filter(s=>!s.parentElement.closest('svg'))) {
        const ctm=shapeSvg.getScreenCTM();
        if(!ctm) continue;
        const matrix=[ctm.a,ctm.b,ctm.c,ctm.d,ctm.e-origin.left,ctm.f-origin.top];
        pieces.push(`<g xmlns="http://www.w3.org/2000/svg" transform="matrix(${matrix.join(' ')})">${Array.from(shapeSvg.childNodes).map(n=>new XMLSerializer().serializeToString(n)).join('')}</g>`);
        shapeSvg.remove();
      }
      element.svg=pieces.join('');
      if(cloned.childNodes.length) element.html=Array.from(cloned.childNodes).map(node=>new XMLSerializer().serializeToString(node)).join('');
    } else if(pureSvg) {
      const r=svg.getBoundingClientRect();
      svg.setAttribute('x',String(r.left-origin.left)); svg.setAttribute('y',String(r.top-origin.top));
      svg.setAttribute('width',String(Math.max(1,r.width))); svg.setAttribute('height',String(Math.max(1,r.height)));
      svg.style.removeProperty('position'); svg.style.removeProperty('left'); svg.style.removeProperty('top'); svg.style.overflow='visible';
      element.svg=new XMLSerializer().serializeToString(svg);
    } else if(cloned.childNodes.length) element.html=Array.from(cloned.childNodes).map(node=>new XMLSerializer().serializeToString(node)).join('');
    if(kind==='unknown') element.sourceFragment=originalAnchorFragment(parsed,anchor,elementId);
    if(!element.svg && !element.html && !element.children?.length) {
      element.text=element.text || `无法绘制 ${nativeType}`;
      addDiagnostic(elementId,'EMPTY_VISUAL','对象没有可识别的可见几何，已保留类型和文字。');
    }
    measure.remove(); return element;
  };
  try {
    if(!topAnchors.length) {
      if(!threads.length && isOfficialEmptyCanvas(parsed)) {
        addDiagnostic('','EMPTY_CANVAS','微软导出包的画布没有对象，显示空画布；这仅反映本次导出内容，不证明云端白板为空。','info');
      } else {
        addDiagnostic('','NO_WHITEBOARD_ANCHORS','HTML 中未找到 data-whiteboard-type 白板对象或可识别的微软空画布结构；请确认这是微软官方白板导出包。','error');
        scene.diagnostics.status='failed';
      }
    } else {
      if(topAnchors.length>50000) throw new Error('白板对象数超过 50,000 个安全导入上限。');
      scene.elements=topAnchors.map(parseAnchor).filter(Boolean);
      scene.bounds=unionRects(collectBounds(scene.elements),24);
      const background=getComputedStyle(canvas).backgroundColor;
      if(background && !['rgba(0, 0, 0, 0)','transparent'].includes(background)) scene.background=background;
    }
    // Preserve unattached comments in the scene rather than silently losing them.
    const attached=new Set(flatten(scene.elements).map(e=>e.commentThreadId).filter(Boolean));
    const unattached=threads.filter(t=>!attached.has(String(t.id)));
    if(unattached.length) scene.unattachedComments=unattached.map(t=>({id:String(t.id),comments:(t.comments||[]).map(c=>({author:String(c.author?.name||'匿名'),body:String(c.body||''),displayDate:String(c.displayDate||'')}))}));
    if(scene.diagnostics.status!=='failed' && (scene.diagnostics.unsupported || scene.diagnostics.missingAssets.length || scene.diagnostics.items.some(d=>d.severity==='warning'||d.severity==='error'))) scene.diagnostics.status='partial';
    return scene;
  } finally {host.remove();}
}
function flatten(elements) {return elements.flatMap(e=>[e,...flatten(e.children||[])]);}
function isOfficialEmptyCanvas(parsed) {
  // Recognize the export skeleton, not a generic div named canvasContent.
  // Inspect the inert source, before sanitization could discard unknown content
  // and incorrectly turn an unsupported canvas into an apparently empty one.
  const canvases=parsed.querySelectorAll('div#canvasContent.contentOrigin[data-blueprint-type="Canvas"][data-identity-key]');
  if(canvases.length!==1 || parsed.querySelector('script,img,canvas,video,iframe,object')) return false;
  const canvas=canvases[0];
  if(!canvas.getAttribute('data-identity-key') || canvas.textContent.trim()) return false;
  // Empty official exports still contain accessibility, cursor, wet-ink, and
  // paint-definition scaffolding. These are not persisted board objects. Do
  // not broadly ignore SVG or div children: a future object could paint there.
  if(!Array.from(canvas.children).every(isEmptyCanvasUtility)) return false;
  if(!['rainbow-pen','galaxy-pen'].every(id=>parsed.getElementById(id)?.closest('svg'))) return false;
  const css=Array.from(parsed.querySelectorAll('style')).map(style=>style.textContent).join('\n');
  return [/\.anchor\b/,/\.inkStroke\b/,/\.canvasChildElement\b/].every(pattern=>pattern.test(css));
}
function isEmptyCanvasUtility(node) {
  if(node.localName==='div' && node.id==='sharedDescribedBy') return !node.childElementCount && !node.textContent.trim();
  if(node.localName==='div' && node.id==='canvasCursor' && node.classList.contains('denyInteraction') && node.getAttribute('aria-hidden')==='true') return !node.childElementCount && !node.textContent.trim();
  if(node.localName==='div' && node.classList.contains('wetInkContainer')) {
    return Array.from(node.children).every(svg=>svg.localName==='svg' && svg.classList.contains('wetInk') && svg.classList.contains('ink') && svg.getAttribute('aria-hidden')==='true' &&
      Array.from(svg.children).every(group=>group.localName==='g' && group.classList.contains('wetInk') && group.classList.contains('ink') && group.getAttribute('aria-hidden')==='true' && !group.childElementCount && !group.textContent.trim()));
  }
  if(node.localName==='svg' && node.id==='effect-resources' && node.classList.contains('includedInExportedPdf')) {
    return Array.from(node.children).every(defs=>defs.localName==='defs' && Array.from(defs.children).every(image=>image.localName==='image' && ['rainbow-pen','galaxy-pen'].includes(image.id) && !image.childElementCount));
  }
  return false;
}
function closestTypedParent(node,limit) {let parent=node.parentElement; while(parent && parent!==limit) {if(parent.hasAttribute('data-whiteboard-type')) return parent; parent=parent.parentElement;} return limit?.hasAttribute('data-whiteboard-type')?limit:null;}
function inlineComputed(original,clone) {
  const computed=getComputedStyle(original),values=[];
  for(const name of PRESENTATION_STYLES) {
    // font shorthand serializes inconsistently; its individual properties survive.
    if(name==='font' || name==='background' || name==='border') continue;
    const value=computed.getPropertyValue(name);
    if(value) values.push(`${name}:${value}`);
  }
  clone.setAttribute('style',sanitizeStyle(values.join(';')));
  for(let i=0;i<original.children.length;i++) inlineComputed(original.children[i],clone.children[i]);
}
function extractText(node) {
  const blocks=Array.from(node.querySelectorAll('[data-block]')).filter(block=>!block.parentElement.closest('[data-block]'));
  if(blocks.length) return blocks.map(block=>block.textContent).join('\n');
  if(['inkgroup','connector','fluidimage','documentpage','reactionstickers'].includes(node.getAttribute('data-whiteboard-type')?.toLowerCase())) return '';
  return node.textContent.trim();
}
function fallbackBounds(node,kind) {
  const box=node.querySelector('.imageComponent,.textBoxBackground,.stickyNote,.plainText,svg') || node;
  const width=finite(parseFloat(box.style.width||box.getAttribute('width')),kind==='note'?304:240),height=finite(parseFloat(box.style.height||box.getAttribute('height')),kind==='note'?305:120);
  return {x:kind==='image'?-width/2:0,y:kind==='image'?-height/2:0,width:Math.max(1,width),height:Math.max(1,height)};
}
function inferMime(url) {return /\.png(?:[?#]|$)/i.test(url)?'image/png':/\.jpe?g(?:[?#]|$)/i.test(url)?'image/jpeg':/\.gif(?:[?#]|$)/i.test(url)?'image/gif':/\.svg(?:[?#]|$)/i.test(url)?'image/svg+xml':/\.webp(?:[?#]|$)/i.test(url)?'image/webp':'';}
function originalAnchorFragment(parsed,node,id) {
  const all=Array.from(parsed.querySelectorAll('[data-whiteboard-type]'));
  const original=all.find(n=>(n.getAttribute('data-whiteboard-id')||n.getAttribute('data-id')||n.id)===id) || all.find(n=>n.getAttribute('data-whiteboard-type')===node.getAttribute('data-whiteboard-type') && n.style.left===node.style.left && n.style.top===node.style.top);
  return original?.outerHTML || node.outerHTML;
}
