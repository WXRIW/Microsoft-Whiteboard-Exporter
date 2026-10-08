// Hand-authored structural fixtures. No customer board data is stored here.
import {readFileSync} from 'node:fs';
export const officialEmptyExport=readFileSync(new URL('./fixtures/empty-export.html',import.meta.url),'utf8');

export function azureImageExport({source='',loading=false,type='AzureImage'}={}) {
  return `<!doctype html><html><body><div id="canvasContent">
<div class="anchor" data-whiteboard-type="${type}" data-id="azure-image" style="left:140px;top:160px">
<div class="canvasChildElement" data-identity-key="synthetic-azure-identity"><div class="imageComponent" style="width:240px;height:180px">
${source?`<img src="${source}" width="240" height="180" alt="合成图片">`:loading?'<div class="ms-Shimmer-container"><div class="ms-Shimmer-shimmerWrapper" style="width:240px;height:180px"><svg width="16" height="16"><path d="M0 0H16V16H0Z" fill="#eee"/></svg></div></div>':'<img alt="导出时没有源地址">'}
</div></div></div></div></body></html>`;
}
