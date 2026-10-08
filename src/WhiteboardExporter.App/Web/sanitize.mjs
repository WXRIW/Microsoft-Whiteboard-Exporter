// Import is parsed inertly. Only presentation markup crosses into the renderer.
// This is also applied to scene.json, which is untrusted input, not executable HTML.
export const PRESENTATION_STYLES = new Set(`display position left top right bottom width height min-width min-height max-width max-height box-sizing overflow overflow-x overflow-y opacity visibility color background background-color background-image background-size background-position background-repeat border border-top border-right border-bottom border-left border-width border-style border-color border-radius border-top-left-radius border-top-right-radius border-bottom-left-radius border-bottom-right-radius box-shadow outline outline-color outline-style outline-width padding padding-top padding-right padding-bottom padding-left margin margin-top margin-right margin-bottom margin-left font font-family font-size font-style font-weight font-variant font-stretch line-height letter-spacing word-spacing text-align text-decoration text-decoration-line text-decoration-color text-decoration-style text-transform text-indent white-space word-break overflow-wrap vertical-align list-style list-style-type list-style-position table-layout border-collapse border-spacing empty-cells flex flex-direction flex-wrap flex-grow flex-shrink flex-basis align-items align-self align-content justify-content gap row-gap column-gap grid-template-columns grid-template-rows grid-auto-flow grid-auto-columns grid-auto-rows grid-column grid-row order transform transform-origin object-fit object-position fill fill-opacity fill-rule stroke stroke-width stroke-opacity stroke-linecap stroke-linejoin stroke-miterlimit stroke-dasharray stroke-dashoffset clip-rule paint-order vector-effect filter clip-path mask isolation mix-blend-mode stop-color stop-opacity`.split(/\s+/));
const HTML_TAGS = new Set(`div span p br strong b em i u s strike sub sup pre code blockquote ol ul li dl dt dd table thead tbody tfoot tr th td caption colgroup col h1 h2 h3 h4 h5 h6 a img hr section article header footer label small mark`.split(' '));
const SVG_TAGS = new Set(`svg g path rect circle ellipse line polyline polygon text tspan defs style lineargradient radialgradient stop pattern clippath mask use image marker filter fegaussianblur feoffset femerge femergenode fecolormatrix`.split(' '));
const SVG_ATTRS = new Set(`id x y x1 y1 x2 y2 cx cy r rx ry width height d points viewbox preserveaspectratio transform fill fill-opacity fill-rule stroke stroke-width stroke-opacity stroke-linecap stroke-linejoin stroke-miterlimit stroke-dasharray stroke-dashoffset clip-path clip-rule mask opacity xmlns xmlns:xlink gradientunits gradienttransform offset stop-color stop-opacity spreadmethod patternunits patterncontentunits patterntransform markerwidth markerheight refx refy orient marker-start marker-mid marker-end filter stddeviation dx dy result in in2 type values text-anchor dominant-baseline alignment-baseline font-family font-size font-style font-weight text-decoration textlength lengthadjust xml:space`.split(' '));

export function safeUrl(value,allowAsset=false) {
  const url=String(value || '').trim();
  if (url.startsWith('#')) return /^#[\w:.-]+$/.test(url) ? url : '';
  if (allowAsset && /^wbasset:[\w.-]+$/.test(url)) return url;
  return safeDataUrl(url);
}
export function sniffMime(data) {
  if (data.startsWith('\x89PNG')) return 'image/png';
  if (data.startsWith('\xff\xd8\xff')) return 'image/jpeg';
  if (data.startsWith('GIF8')) return 'image/gif';
  if (data.startsWith('RIFF') && data.slice(8,12)==='WEBP') return 'image/webp';
  if (/^\s*(?:<\?xml[^>]*>\s*)?<svg[\s>]/i.test(data)) return 'image/svg+xml';
  if (data.startsWith('BM')) return 'image/bmp';
  return '';
}
export function safeDataUrl(value) {
  if (!/^data:/i.test(value)) return '';
  const comma=value.indexOf(',');
  if (comma<0 || value.length>150_000_000) return '';
  try {
    const header=value.slice(0,comma), binary=/;base64$/i.test(header) ? atob(value.slice(comma+1).replace(/\s+/g,'')) : decodeURIComponent(value.slice(comma+1));
    const mime=sniffMime(binary);
    if (!mime) return '';
    if (mime==='image/svg+xml') {
      const svgText=/;base64$/i.test(header) ? new TextDecoder().decode(Uint8Array.from(binary,c=>c.charCodeAt(0))) : binary;
      const document=new DOMParser().parseFromString(svgText,'image/svg+xml');
      if (document.querySelector('parsererror') || document.documentElement.localName.toLowerCase()!=='svg') return '';
      const cleaned=sanitizeNode(document.documentElement,{allowAsset:false});
      const xml=new XMLSerializer().serializeToString(cleaned);
      // Unicode-safe base64, rather than btoa on source Chinese glyphs.
      const bytes=new TextEncoder().encode(xml);
      let packed=''; for (let i=0;i<bytes.length;i+=8192) packed+=String.fromCharCode(...bytes.subarray(i,i+8192));
      return `data:${mime};base64,${btoa(packed)}`;
    }
    return `data:${mime};base64,${/;base64$/i.test(header) ? value.slice(comma+1).replace(/\s+/g,'') : btoa(binary)}`;
  } catch { return ''; }
}
export function sanitizeStyle(value,{allowAsset=false}={}) {
  const parser=document.createElement('span').style;
  parser.cssText=String(value || '');
  const result=[];
  for (const name of parser) {
    if (!PRESENTATION_STYLES.has(name)) continue;
    let val=parser.getPropertyValue(name);
    if(name==='position' && val==='fixed') val='absolute';
    if(name==='position' && val==='sticky') val='relative';
    // Escapes and old executable CSS syntax have no role in a saved board.
    if (/[\\]|expression\s*\(|-moz-binding|javascript:|@import|behavior\s*:/i.test(val)) continue;
    let rejected=false;
    val=val.replace(/url\(\s*(?:"([^"]*)"|'([^']*)'|([^)]*))\s*\)/gi,(_,a,b,c)=>{
      const safe=safeUrl(a ?? b ?? c,allowAsset);
      if (!safe) { rejected=true; return 'none'; }
      return `url("${safe.replace(/"/g,'%22')}")`;
    });
    if (!rejected && val) result.push(`${name}:${val}${parser.getPropertyPriority(name)==='important'?' !important':''}`);
  }
  return result.join(';');
}
export function sanitizeStylesheet(value) {
  try {
    const sheet=new CSSStyleSheet(); sheet.replaceSync(String(value||''));
    const read=rules=>Array.from(rules).flatMap(rule=>{
      if (rule.type===1 && !/[\\@{}<>]/.test(rule.selectorText)) {
        const declaration=sanitizeStyle(rule.style.cssText);
        // html/body rules should affect only the isolated source root.
        return declaration ? [`${rule.selectorText.replace(/\b(?:html|body)\b/g,'.source-root')}{${declaration}}`] : [];
      }
      // Match screen rules only; print styles must not change source measurements.
      if (rule.type===4 && matchMedia(rule.conditionText).matches) return read(rule.cssRules);
      return [];
    });
    return read(sheet.cssRules).join('\n');
  } catch { return ''; }
}
export function sanitizeNode(node,{allowAsset=false,preserveSources=false,allowForeignObject=false}={}) {
  if (node.nodeType===3) return document.createTextNode(node.textContent);
  if (node.nodeType!==1) return null;
  const name=node.localName.toLowerCase(), svg=node.namespaceURI==='http://www.w3.org/2000/svg';
  if (!(svg ? SVG_TAGS : HTML_TAGS).has(name) && !(svg && name==='foreignobject' && allowForeignObject)) return null;
  // Scene fragments exclude foreignObject by default. A standalone saved SVG
  // may retain this container; each child still receives HTML sanitization.
  const copy=svg ? document.createElementNS('http://www.w3.org/2000/svg',node.localName) : document.createElement(name);
  if(svg && name==='style') {copy.textContent=sanitizeStylesheet(node.textContent);return copy;}
  for (const attribute of node.attributes) {
    const key=attribute.name.toLowerCase(), value=attribute.value;
    if (key==='style') { const style=sanitizeStyle(value,{allowAsset}); if (style) copy.setAttribute('style',style); }
    else if (key==='class' || key==='title' || key==='lang' || key==='dir' || key==='role' || key.startsWith('aria-') || key.startsWith('data-')) copy.setAttribute(attribute.name,value);
    else if (key==='id') copy.setAttribute('id',value);
    else if (key==='src' || key==='href' || key==='xlink:href') {
      if (name==='a') {
        // URL cards display the address as text; they do not navigate the app.
        if (/^https?:\/\//i.test(value)) copy.setAttribute('data-link-url',value);
      } else {
        const safe=safeUrl(value,allowAsset);
        if (safe) {
          if(key==='xlink:href') copy.setAttributeNS('http://www.w3.org/1999/xlink','xlink:href',safe);
          else copy.setAttribute(attribute.name,safe);
        }
        else if (preserveSources) copy.setAttribute('data-wb-source',value);
      }
    } else if (svg && name==='foreignobject' && allowForeignObject && key==='overflow' && /^(visible|hidden|clip|auto|scroll)$/.test(value)) copy.setAttribute(attribute.name,value);
    else if (svg && SVG_ATTRS.has(key) && !/javascript:|url\(\s*["']?(?!#)/i.test(value)) copy.setAttribute(attribute.name,value);
    else if (!svg && ['width','height','alt','colspan','rowspan','start','type'].includes(key)) copy.setAttribute(attribute.name,value);
  }
  for (const child of node.childNodes) {
    const cleaned=sanitizeNode(child,{allowAsset,preserveSources,allowForeignObject}); if (cleaned) copy.appendChild(cleaned);
  }
  return copy;
}
export function sanitizeMarkup(markup,svg=false) {
  const parsed=new DOMParser().parseFromString(svg ? `<svg xmlns="http://www.w3.org/2000/svg">${markup || ''}</svg>` : String(markup||''),svg?'image/svg+xml':'text/html');
  if (parsed.querySelector('parsererror')) return '';
  // Scene fragments use inline presentation styles. An imported stylesheet
  // must never escape a fragment and restyle the local renderer's document.
  for(const stylesheet of parsed.querySelectorAll('style')) stylesheet.remove();
  const holder=svg ? parsed.documentElement : parsed.body;
  return Array.from(holder.childNodes).map(node=>sanitizeNode(node,{allowAsset:true})).filter(Boolean).map(node=>node.nodeType===3 ? escapeText(node.textContent) : new XMLSerializer().serializeToString(node)).join('');
}
const escapeText=value=>String(value||'').replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
