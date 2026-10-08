import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

// Exercise the scripts shipped in the WPF service, rather than a copied test
// implementation. These synthetic DOMs contain no account or whiteboard data.
// Run: node --test tests/js/cloud-readiness.test.mjs
const source=readFileSync(new URL('../../src/WhiteboardExporter.App/Cloud/CloudWhiteboardService.cs',import.meta.url),'utf8');
function rawString(pattern,label) {
  const match=source.match(pattern);
  assert.ok(match,`Cannot extract current ${label}; update the extractor if C# syntax changes.`);
  const [,body,indent]=match;
  return body.split(/\r?\n/).map(line=>line.startsWith(indent)?line.slice(indent.length):line).join('\n');
}
const helpers=rawString(/private const string Helpers\s*=\s*"""\r?\n([\s\S]*?)\r?\n([ \t]*)""";/,'Helpers');
const stateBody=rawString(/internal static readonly string ReadEditorState\s*=\s*"\(\(\) => \{"\s*\+\s*Helpers\s*\+\s*"""\r?\n([\s\S]*?)\r?\n([ \t]*)""";/,'ReadEditorState');
const stateScript='(() => {'+helpers+stateBody;
const cleanupMatch=source.match(/internal const string EndEditorWait\s*=\s*("(?:\\.|[^"\\])*");/);
assert.ok(cleanupMatch,'Cannot extract current EndEditorWait.');
const cleanupScript=JSON.parse(cleanupMatch[1]);

// Minimal CSS/DOM surface needed by the actual synchronous readiness scripts.
// It deliberately models layout/visibility without loading a browser, network,
// or third-party DOM library. This checks structural decisions, not real login.
class Element {
  constructor(tag='div',options={}) {
    this.tagName=tag.toLowerCase();
    this.id=options.id??'';
    this.className=options.className??'';
    this.attributes={...options.attributes};
    this.style={display:'block',visibility:'visible',opacity:'1',overflow:'visible',overflowX:'visible',overflowY:'visible',...options.style};
    this.rect={width:options.width??100,height:options.height??40};
    this.children=[];
    this.parentElement=null;
    this.complete=options.complete??true;
    this.naturalWidth=options.naturalWidth??100;
  }
  append(...children) {for(const child of children){child.parentElement=this;this.children.push(child);}return this;}
  getBoundingClientRect() {return {...this.rect};}
  getAttribute(name) {return name==='class'?this.className:name==='id'?this.id:this.attributes[name]??null;}
  matches(selector) {
    return selector.split(',').some(part=>{
      part=part.trim();
      const attributes=[...part.matchAll(/\[([\w-]+)(?:(\*=|=)["']([^"']*)["'])?\]/g)];
      const base=part.replace(/\[[^\]]*\]/g,'');
      const tag=base.match(/^[\w-]+/);
      if(tag&&tag[0]!==this.tagName)return false;
      const id=base.match(/#([\w-]+)/);
      if(id&&id[1]!==this.id)return false;
      const classes=[...base.matchAll(/\.([\w-]+)/g)].map(match=>match[1]);
      if(classes.some(name=>!this.className.split(/\s+/).includes(name)))return false;
      return attributes.every(([,name,operator,value])=>{
        const actual=this.getAttribute(name);
        if(!operator)return actual!==null;
        return actual!==null&&(operator==='='?actual===value:actual.includes(value));
      });
    });
  }
  querySelectorAll(selector) {
    const result=[];
    const walk=element=>{for(const child of element.children){if(child.matches(selector))result.push(child);walk(child);}};
    walk(this);return result;
  }
  querySelector(selector) {return this.querySelectorAll(selector)[0]??null;}
  click() {this.clicked=true;}
}
const element=(tag,options)=>new Element(tag,options);
function harness({canvas=true}={}) {
  const body=element('body',{width:1280,height:900});
  const settings=element('button',{id:'settingsButton'});
  body.append(settings);
  const surface=canvas?element('div',{id:'canvasContent',width:1000,height:700}):null;
  if(surface)body.append(surface);
  const observers=[];
  class MutationObserver {
    constructor(callback){this.callback=callback;this.connected=false;observers.push(this);}
    observe(target,options){this.target=target;this.options=options;this.connected=true;}
    disconnect(){this.connected=false;}
  }
  const document={body,querySelectorAll:selector=>body.querySelectorAll(selector),querySelector:selector=>body.querySelector(selector)};
  const context=vm.createContext({document,window:{},innerWidth:1280,innerHeight:900,getComputedStyle:e=>e.style,MutationObserver});
  return {body,settings,surface,observers,context,
    read:()=>JSON.parse(JSON.stringify(vm.runInContext(stateScript,context,{timeout:1000}))),
    cleanup:()=>vm.runInContext(cleanupScript,context,{timeout:1000})};
}

test('settings shell and toolbar SVG alone do not imply a ready canvas',()=>{
  const h=harness({canvas:false});
  h.body.append(element('svg',{width:24,height:24}));
  const state=h.read();
  assert.equal(state.editor,true);assert.equal(state.canvas,false);assert.equal(state.loading,false);
  assert.equal(state.objects,0);assert.ok(h.context.window.__wbExporterEditorWait);
});

test('a visible blank canvas remains eligible and has a stable structural stamp',()=>{
  const h=harness();
  h.surface.append(element('div',{id:'sharedDescribedBy'}),element('svg',{id:'canvasBackground',width:1000,height:700}),element('svg',{id:'wetInk',width:1000,height:700}));
  const first=h.read(),second=h.read();
  assert.equal(first.canvas,true);assert.equal(first.loading,false);assert.equal(first.objects,0);
  assert.equal(first.stamp,second.stamp);assert.equal(h.observers.length,1);
});

test('zero-sized canvasContent falls back to a large wet ink SVG and clipping viewport',()=>{
  const h=harness({canvas:false});
  const viewport=element('div',{width:1000,height:700,style:{overflow:'hidden'}});
  viewport.append(element('div',{id:'canvasContent',width:0,height:0}),element('svg',{id:'wetInk',width:1000,height:700}));
  h.body.append(viewport);
  assert.equal(h.read().canvas,true);
  assert.equal(h.context.window.__wbExporterEditorWait.surface,viewport);
});

test('visible progress and busy indicators block readiness; hidden indicators do not',()=>{
  const h=harness();
  for(const options of [{className:'ms-Spinner'},{attributes:{role:'progressbar'}},{attributes:{'aria-busy':'true'}}]) {
    const indicator=element('div',options);h.surface.append(indicator);
    assert.equal(h.read().loading,true);
    indicator.style.display='none';assert.equal(h.read().loading,false);
  }
});

test('shimmer wrappers block while visible and ignore retained or hidden containers',()=>{
  const h=harness();
  const container=element('div',{className:'ms-Shimmer-container'});
  const wrapper=element('div',{className:'ms-Shimmer-shimmerWrapper'});
  container.append(wrapper);h.surface.append(container);
  assert.equal(h.read().loading,true);
  wrapper.style.opacity='0';assert.equal(h.read().loading,false);
  wrapper.style.opacity='1';container.style.opacity='0';assert.equal(h.read().loading,false);
  container.style.opacity='1';assert.equal(h.read().loading,true);
});

test('only visible pending images block; completed broken images still allow source capture',()=>{
  const h=harness(),image=element('img',{complete:false});h.surface.append(image);
  assert.equal(h.read().loading,true);
  image.style.visibility='hidden';assert.equal(h.read().loading,false);
  image.style.visibility='visible';image.complete=true;assert.equal(h.read().loading,false);
  // complete=true with naturalWidth=0 is a failed resource, not ongoing hydration.
  // Waiting forever cannot repair it; preserve the ZIP for importer diagnostics.
  image.naturalWidth=0;assert.equal(h.read().loading,false);
});

test('live ink, typed anchors and text are counted without returning their content',()=>{
  const h=harness();
  h.surface.append(element('svg',{className:'inkGroup'}),element('div',{className:'anchor',attributes:{'data-whiteboard-type':'StickyNote'}}),element('div',{className:'DraftEditor-editorContainer'}));
  const state=h.read();assert.equal(state.objects,3);
  assert.deepEqual(Object.keys(state).sort(),['canvas','editor','loading','objects','stamp']);
  assert.equal(state.loading,false);
});

test('canvas mutations advance the stamp and observer ignores animation-only styles',()=>{
  const h=harness(),first=h.read(),observer=h.observers[0];
  observer.callback();assert.notEqual(h.read().stamp,first.stamp);
  assert.equal(observer.target,h.surface);assert.equal(observer.options.subtree,true);
  assert.ok(observer.options.attributeFilter.includes('src'));
  assert.ok(observer.options.attributeFilter.includes('d'));
  assert.ok(!observer.options.attributeFilter.includes('style'));
});

test('replacing the surface disconnects the old observer and observes the new canvas',()=>{
  const h=harness();h.read();const previous=h.observers[0];
  h.surface.rect={width:0,height:0};
  const replacement=element('canvas',{width:900,height:600});h.body.append(replacement);
  assert.equal(h.read().canvas,true);assert.equal(previous.connected,false);
  assert.equal(h.context.window.__wbExporterEditorWait.surface,replacement);
  assert.equal(h.observers.length,2);assert.equal(h.observers[1].connected,true);
});

test('cleanup disconnects and removes editor state, and is safe when repeated',()=>{
  const h=harness();h.read();const observer=h.observers[0];
  assert.equal(h.cleanup(),true);assert.equal(observer.connected,false);
  assert.equal(h.context.window.__wbExporterEditorWait,undefined);
  assert.equal(h.cleanup(),true);assert.equal(h.read().canvas,true);
  assert.equal(h.observers.length,2);
});
