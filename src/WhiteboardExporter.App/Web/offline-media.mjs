import {sanitizeNode,safeDataUrl} from './sanitize.mjs';

const SVG_NS='http://www.w3.org/2000/svg';
const number=value=>{
  const text=String(value||'').trim();
  return /^[+]?(?:\d+(?:\.\d*)?|\.\d+)(?:px)?$/i.test(text)?Number.parseFloat(text):NaN;
};
const missingImage='data:image/svg+xml;charset=utf-8,'+encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="300" height="180" viewBox="0 0 300 180"><rect width="300" height="180" fill="#f3f5f8" stroke="#a8b4c5"/><path d="M20 20L280 160M280 20L20 160" stroke="#a8b4c5" stroke-width="4"/></svg>');

/** Parse a saved visual inertly; a media preview is not a SceneDocument import. */
export function prepareOfflineSvg({markup,format='svg',title='本地导出文件'}={}) {
  markup=String(markup||'');
  if(format==='svg' && /<!DOCTYPE|<!ENTITY/i.test(markup))throw new Error('SVG 不能包含 DTD 或自定义实体。');
  const parsed=new DOMParser().parseFromString(markup,format==='svg'?'image/svg+xml':'text/html');
  if(parsed.querySelector('parsererror'))throw new Error('SVG 文件结构损坏。');
  const candidates=Array.from(parsed.querySelectorAll('svg')).filter(svg=>!svg.parentElement?.closest('svg'));
  if(candidates.length!==1)throw new Error('此文件没有唯一的白板 SVG；请选择该白板的 PDF、图片或原始备份。');
  const source=candidates[0];
  if(source.namespaceURI!==SVG_NS)throw new Error('SVG 命名空间无效。');
  const removedActiveContent=source.querySelectorAll('script,iframe,object,embed,form,input,button,video,audio').length+
    [source,...source.querySelectorAll('*')].reduce((total,node)=>total+Array.from(node.attributes).filter(attribute=>/^on/i.test(attribute.name)).length,0);
  let blockedResources=0;
  for(const image of source.querySelectorAll('image,img')) {
    const src=image.getAttribute('href')||image.getAttribute('xlink:href')||image.getAttribute('src');
    if(src && !safeDataUrl(src)) {
      blockedResources++;
      image.removeAttribute('src');image.removeAttribute('href');image.removeAttribute('xlink:href');
      image.setAttribute(image.localName.toLowerCase()==='img'?'src':'href',missingImage);
    }
  }
  // Saved exports use inline presentation. No imported sheet may restyle the
  // renderer page, and no source script or relative file is ever navigated.
  for(const style of source.querySelectorAll('style'))style.remove();
  const svg=sanitizeNode(source,{allowForeignObject:true});
  if(!svg)throw new Error('无法显示此 SVG 文件。');
  let values=String(svg.getAttribute('viewBox')||'').trim().split(/[\s,]+/).map(Number);
  if(values.length!==4 || !values.every(Number.isFinite) || values[2]<=0 || values[3]<=0) {
    const width=number(svg.getAttribute('width')),height=number(svg.getAttribute('height'));
    if(!Number.isFinite(width)||!Number.isFinite(height)||width<=0||height<=0)throw new Error('SVG 缺少有效画板尺寸。');
    values=[0,0,width,height];
  }
  if(values.some(value=>Math.abs(value)>1e9))throw new Error('SVG 画板尺寸超出预览范围。');
  const bounds={x:values[0],y:values[1],width:values[2],height:values[3]};
  svg.setAttribute('viewBox',values.join(' '));svg.setAttribute('width',String(bounds.width));svg.setAttribute('height',String(bounds.height));
  if(!svg.hasAttribute('preserveAspectRatio'))svg.setAttribute('preserveAspectRatio','xMidYMid meet');
  svg.style.display='block';svg.style.overflow='hidden';
  title=String(source.getAttribute('aria-label')||parsed.querySelector('title')?.textContent||title).trim().slice(0,512)||'本地导出文件';
  return {svg,bounds,title,blockedResources,removedActiveContent};
}
