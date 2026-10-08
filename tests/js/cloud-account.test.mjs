import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

const source=readFileSync(new URL('../../src/WhiteboardExporter.App/Cloud/CloudWhiteboardService.cs',import.meta.url),'utf8');
const match=source.match(/private const string AccountReader\s*=\s*"""\r?\n([\s\S]*?)\r?\n([ \t]*)""";/);
assert.ok(match,'Extract the actual shipped account reader');
const reader=match[1].split(/\r?\n/).map(line=>line.startsWith(match[2])?line.slice(match[2].length):line).join('\n');
const script='(() => {'+reader+'return readAccount().identity;})()';
const probeScript='(() => {'+reader+'return readAccount().probe;})()';
assert.ok(source.includes('ReadAccount = "(() => {" + AccountReader + "return readAccount().identity;})()"'));
assert.ok(source.includes('ReadAccountProbe = "(() => {" + AccountReader + "return readAccount().probe;})()"'));
const tenant='00000000-0000-4000-8000-000000000007';
const msaTenant='9188040d-6c67-4c5b-b112-36a304b66dad';
const active=(overrides={})=>({authenticatedState:'signedIn',type:'msa',memberName:'Person@Example.invalid',displayName:'测试用户',tenantId:tenant,...overrides});
const user=(overrides={})=>({displayName:'当前姓名',upn:'current@example.invalid',tenantId:msaTenant,...overrides});

function bindHeader(header,currentUser,{contextName='ServiceContextContext',providerExtra={},componentExtra={}}={}){
  const root={return:null};root.stateNode={current:root};
  const provider={_userInfoCache:currentUser};
  Object.defineProperties(provider,Object.getOwnPropertyDescriptors(providerExtra));
  const context={userInfoProvider:provider};
  const component={return:root,dependencies:{firstContext:{context:{displayName:contextName},memoizedValue:context,next:null}}};
  Object.defineProperties(component,Object.getOwnPropertyDescriptors(componentExtra));
  const fiber={return:component,stateNode:header};root.child=component;component.child=fiber;header.__reactFiber$accountTest=fiber;
  return {root,component,fiber,context,provider};
}

function domPanel({email='visible@example.invalid',name='可见姓名',href='https://account.microsoft.com/?ref=MeControl',visible=true}={}){
  const node=textContent=>({textContent,getBoundingClientRect:()=>({width:visible?80:0,height:visible?20:0})});
  const emailNode=node(email),nameNode=node(name),link={getAttribute:()=>href};
  return {...node(''),querySelector:selector=>selector==='#mectrl_currentAccount_secondary'?emailNode:selector==='#mectrl_currentAccount_primary'?nameNode:selector==='a[id^="mectrl_viewAccount"][href]'?link:null};
}

function read({account=active(),host='whiteboard.cloud.microsoft',protocol='https:',header=true,gallery=false,getOptions=true,options={},headerUser,galleryUser,modelOptions={},panels=[],configureHeader,probe=false}={}){
  let calls=0;
  const config={currentAccount:account};
  Object.defineProperties(config,Object.getOwnPropertyDescriptors(options));
  const window={MeControl:{WebInline:getOptions?{getOptions:()=>{calls++;return config;}}:{}}};
  const headerNode={querySelectorAll:selector=>selector==='.mectrl_currentAccount'?panels:[]};
  const galleryNode={querySelectorAll:()=>[]};
  if(headerUser!==undefined)bindHeader(headerNode,headerUser,modelOptions);
  if(galleryUser!==undefined)bindHeader(galleryNode,galleryUser,modelOptions);
  if(configureHeader)configureHeader(headerNode);
  const document={querySelector:selector=>selector==='.meControlContainerClass'&&header?headerNode:selector==='.scrollableGrid'&&gallery?galleryNode:null};
  const result=vm.runInNewContext(probe?probeScript:script,{window,document,location:{hostname:host,protocol,href:'https://'+host+'/'},URL,getComputedStyle:()=>({display:'block',visibility:'visible'})},{timeout:1000});
  return {value:JSON.parse(JSON.stringify(result)),calls};
}

test('active Microsoft account returns only safe current account metadata',()=>{
  const {value,calls}=read();
  assert.deepEqual(value,{displayName:'测试用户',email:'Person@Example.invalid',tenantId:tenant,kind:'msa',source:'microsoft-mecontrol:currentAccount'});
  assert.equal(calls,1);
});

test('organizational account keeps tenant and kind distinct',()=>{
  assert.equal(read({account:active({type:'aad'})}).value.kind,'aad');
  assert.equal(read({account:active({type:'aad'})}).value.tenantId,tenant);
});

test('remembered accounts and transport settings are never inspected',()=>{
  const config={};
  Object.defineProperty(config,'rememberedAccounts',{get(){throw new Error('Do not inspect cached accounts');}});
  Object.defineProperty(config,'authProviderConfig',{get(){throw new Error('Do not inspect authentication providers');}});
  const account=active();
  Object.defineProperty(account,'accessToken',{get(){throw new Error('Do not read tokens');}});
  Object.defineProperty(account,'cookie',{get(){throw new Error('Do not read cookies');}});
  Object.defineProperty(account,'pictureUrl',{get(){throw new Error('Do not read transport URLs');}});
  const {value}=read({account,options:config});
  assert.deepEqual(Object.keys(value).sort(),['displayName','email','kind','source','tenantId']);
});

test('signed out, IDP-only and unknown account types stay unidentified',()=>{
  for(const account of [null,{},active({authenticatedState:'notSignedIn'}),active({authenticatedState:'signedInIDPOnly'}),active({type:'unknown'})])
    assert.equal(read({account}).value,null);
});

test('no email is guessed from display name, comments, board owners or other accounts',()=>{
  for(const memberName of [null,undefined,'','Display Person','one@example.invalid; two@example.invalid','bad\n@example.invalid','a'.repeat(321)+'@example.invalid'])
    assert.equal(read({account:active({memberName,displayName:'someone@example.invalid'}),options:{rememberedAccounts:[active()]}}).value,null);
});

test('login and external pages are not queried even when they carry account-shaped values',()=>{
  for(const options of [{host:'login.microsoftonline.com'},{host:'evil.invalid'},{host:'whiteboard.cloud.microsoft.evil.invalid'},{protocol:'http:'},{header:false}]){
    const result=read(options);assert.equal(result.value,null);assert.equal(result.calls,0);
  }
});

test('missing getter yields null without invoking unrelated API methods',()=>{
  assert.equal(read({getOptions:false}).value,null);
});

test('empty display name falls back to memberName and invalid tenant is not copied',()=>{
  const {value}=read({account:active({displayName:'   ',memberName:' person@example.invalid ',tenantId:'access-token-shaped-value'})});
  assert.equal(value.displayName,'person@example.invalid');assert.equal(value.email,'person@example.invalid');assert.equal(value.tenantId,null);
});

test('a changed current account is read fresh and remembered identities cannot take precedence',()=>{
  const first=read({account:active(),options:{rememberedAccounts:[active({memberName:'other@example.invalid'})]}}).value;
  const second=read({account:active({memberName:'next@example.invalid'})}).value;
  assert.notEqual(first.email,second.email);assert.equal(second.email,'next@example.invalid');
});

test('committed Header userInfo cache works with closed account popup and missing loader getter',()=>{
  const {value}=read({headerUser:user(),getOptions:false});
  assert.deepEqual(value,{displayName:'当前姓名',email:'current@example.invalid',tenantId:msaTenant,kind:'msa',source:'microsoft-whiteboard:header-userInfoCache'});
});

test('Header model distinguishes both Microsoft consumer tenants and organizational tenant',()=>{
  for(const tenantId of [msaTenant,'4925308c-f164-4d2d-bc7e-0631132e9375'])assert.equal(read({headerUser:user({tenantId})}).value.kind,'msa');
  assert.equal(read({headerUser:user({tenantId:tenant})}).value.kind,'aad');
  assert.equal(read({headerUser:user({tenantId:tenant})}).value.tenantId,tenant);
});

test('current Header takes precedence over stale options and stale visible account details',()=>{
  const value=read({headerUser:user(),account:active({memberName:'old@example.invalid'}),panels:[domPanel({email:'older@example.invalid'})]}).value;
  assert.equal(value.email,'current@example.invalid');assert.equal(value.source,'microsoft-whiteboard:header-userInfoCache');
});

test('signed-out committed Header cache cannot be replaced by stale signed-in options or DOM',()=>{
  assert.equal(read({headerUser:null,panels:[domPanel()]}).value,null);
});

test('unrelated contexts, owner-like props, pending props and arbitrary auth state are not inspected',()=>{
  const componentExtra={};
  for(const name of ['memoizedState','pendingProps','stateNode'])Object.defineProperty(componentExtra,name,{get(){throw new Error('Do not inspect '+name);}});
  const providerExtra={};
  for(const name of ['tokenCache','accessToken','_pca','_proofToken','userInfo'])Object.defineProperty(providerExtra,name,{get(){throw new Error('Do not inspect '+name);}});
  const current=user();for(const name of ['accessToken','cookie','pictureUrl'])Object.defineProperty(current,name,{get(){throw new Error('Do not read '+name);}});
  assert.equal(read({headerUser:current,modelOptions:{componentExtra,providerExtra}}).value.email,'current@example.invalid');
  assert.equal(read({headerUser:current,modelOptions:{contextName:'OtherContext'},account:null}).value,null);
});

test('committed alternate is selected instead of the stale Header fiber',()=>{
  const value=read({configureHeader:header=>{
    const current=bindHeader(header,user());const stale=bindHeader({},user({upn:'stale@example.invalid'}));
    stale.root.stateNode.current=current.root;stale.fiber.alternate=current.fiber;header.__reactFiber$accountTest=stale.fiber;
  }}).value;
  assert.equal(value.email,'current@example.invalid');
});

test('uncommitted and cyclic Header fiber cannot contribute identity',()=>{
  for(const cyclic of [false,true]){
    const value=read({account:null,configureHeader:header=>{
      const current=bindHeader(header,user());current.root.stateNode.current={};
      if(cyclic)current.root.return=current.fiber;
    }}).value;
    assert.equal(value,null);
  }
});

test('visible current account DOM works without loader getter or Header context',()=>{
  const value=read({account:null,getOptions:false,panels:[domPanel()]}).value;
  assert.deepEqual(value,{displayName:'可见姓名',email:'visible@example.invalid',tenantId:null,kind:'msa',source:'microsoft-mecontrol:currentAccount-dom'});
});

test('current organizational DOM uses only the explicit tenant from official account link',()=>{
  const value=read({getOptions:false,panels:[domPanel({href:'https://myaccount.microsoft.com/?tid='+tenant+'&login_hint=unrelated@example.invalid'})]}).value;
  assert.equal(value.kind,'aad');assert.equal(value.tenantId,tenant);assert.equal(value.email,'visible@example.invalid');
  assert.equal(read({getOptions:false,panels:[domPanel({href:'https://myaccount.microsoft.com/'})]}).value,null);
});

test('hidden, ambiguous, malformed or unofficial account panels do not identify remembered users',()=>{
  for(const panels of [[domPanel({visible:false})],[domPanel(),domPanel({email:'second@example.invalid'})],[domPanel({email:'invalid user'})],[domPanel({href:'https://account.microsoft.com.evil.invalid/'})],[domPanel({href:'http://account.microsoft.com/'})],[domPanel({href:'https://account.microsoft.com:444/'})]])
    assert.equal(read({account:null,panels}).value,null);
});

test('valid DOM can fill incomplete current Header metadata but not consume stale options',()=>{
  const result=read({headerUser:user({tenantId:null}),panels:[domPanel()]});
  assert.equal(result.value.source,'microsoft-mecontrol:currentAccount-dom');
  assert.equal(read({headerUser:user({tenantId:null})}).value,null);
});

test('account probe contains structural booleans and a source enum only',()=>{
  const {value}=read({headerUser:user(),probe:true,panels:[domPanel()]});
  assert.deepEqual(value,{whiteboardPage:true,headerPresent:true,galleryPresent:false,getterPresent:true,configurationValid:true,reactFiberPresent:true,committedFiberFound:true,serviceContextPresent:true,userInfoProviderPresent:true,committedAncestorCount:3,contextDependencyCount:1,committedSearchNodes:3,committedHeaderModelPresent:true,headerModelValid:true,currentDomPresent:true,currentDomValid:true,source:'header-model'});
  for(const [key,item] of Object.entries(value))assert.equal(typeof item,key==='source'?'string':['committedAncestorCount','contextDependencyCount','committedSearchNodes'].includes(key)?'number':'boolean');
  assert.equal(JSON.stringify(value).includes('example.invalid'),false);
});

test('O365 gallery identifies current service-context user without a MeControl header or popup',()=>{
  const value=read({header:false,gallery:true,galleryUser:user(),getOptions:false}).value;
  assert.equal(value.email,'current@example.invalid');assert.equal(value.source,'microsoft-whiteboard:header-userInfoCache');
  const probe=read({header:false,gallery:true,galleryUser:user(),getOptions:false,probe:true}).value;
  assert.equal(probe.headerPresent,false);assert.equal(probe.galleryPresent,true);assert.equal(probe.headerModelValid,true);
});

test('named service context may expose the published userInfoProvider as a lazy getter only',()=>{
  const value=read({configureHeader:header=>{
    const binding=bindHeader(header,user());const provider=binding.provider;
    Object.defineProperty(binding.context,'userInfoProvider',{get:()=>provider});
    Object.defineProperty(binding.context,'authenticationProvider',{get(){throw new Error('Do not inspect auth provider');}});
  }}).value;
  assert.equal(value.email,'current@example.invalid');
});

test('long display name and long email fallback respect Core label limit without changing account email',()=>{
  const email='a'.repeat(300)+'@example.invalid';
  const named=read({headerUser:user({displayName:'名'.repeat(256),upn:email})}).value;
  assert.equal(named.displayName,'名'.repeat(200));assert.equal(named.email,email);
  const fallback=read({headerUser:user({displayName:'',upn:email})}).value;
  assert.equal(fallback.displayName.length,200);assert.equal(fallback.email,email);
  const supplementary=read({headerUser:user({displayName:'x'.repeat(199)+'😀'.repeat(10)})}).value;
  assert.equal(supplementary.displayName,'x'.repeat(199));
});

test('committed ServiceContext provider ancestor works when gallery component does not consume the context',()=>{
  const value=read({header:false,gallery:true,galleryUser:user(),getOptions:false,modelOptions:{componentExtra:{dependencies:null,type:{_context:{displayName:'ServiceContextContext'}},memoizedProps:{value:{userInfoProvider:{_userInfoCache:user()}}}}}}).value;
  assert.equal(value.email,'current@example.invalid');assert.equal(value.source,'microsoft-whiteboard:header-userInfoCache');
});

test('stable gallery DOM mounted under old root resolves through current committed structural path',()=>{
  let visitedSecrets=0;
  const {value}=read({getOptions:false,configureHeader:header=>{
    const stale=bindHeader({},user({upn:'stale@example.invalid'}));
    const current=bindHeader({},user({upn:'current@example.invalid'}));
    stale.root.stateNode.current=current.root;
    const retainedHost={return:stale.component,stateNode:header};
    current.root.child=current.component;current.component.child=retainedHost;
    Object.defineProperty(retainedHost,'memoizedState',{get(){visitedSecrets++;throw new Error('Do not inspect retained auth state');}});
    header.__reactFiber$accountTest=retainedHost;
  }});
  assert.equal(value.email,'current@example.invalid');assert.equal(visitedSecrets,0);
});

test('React 19 direct named ServiceContext provider is accepted without reading anonymous context',()=>{
  const value=read({header:false,gallery:true,galleryUser:user(),getOptions:false,modelOptions:{componentExtra:{dependencies:null,type:{displayName:'ServiceContextContext'},memoizedProps:{value:{userInfoProvider:{_userInfoCache:user()}}}}}}).value;
  assert.equal(value.email,'current@example.invalid');
});

test('published web MSAL current _userInfo cache works without reading account claims or lazy identity getter',()=>{
  const {value}=read({getOptions:false,configureHeader:header=>{
    const {provider}=bindHeader(header,user());delete provider._userInfoCache;provider._userInfo=user();
    for(const field of ['_account','_context','userInfo','accessToken','idTokenClaims'])Object.defineProperty(provider,field,{get(){throw new Error('Do not read '+field);}});
  }});
  assert.equal(value.email,'current@example.invalid');assert.equal(value.kind,'msa');
  assert.equal(value.source,'microsoft-whiteboard:header-userInfo');
});

test('web MSAL signed-out cached profile cannot be replaced by remembered currentAccount options',()=>{
  const {value}=read({configureHeader:header=>{const {provider}=bindHeader(header,user());delete provider._userInfoCache;provider._userInfo=null;}});
  assert.equal(value,null);
});

test('same current root with stale ancestor return still resolves profile through committed child path',()=>{
  const {value}=read({getOptions:false,configureHeader:header=>{
    const current=bindHeader(header,user());
    const stale=bindHeader({},user({upn:'stale@example.invalid'}));
    stale.component.return=current.root;
    current.fiber.return=stale.component;
    // The retained host's stale return reaches the current root, but the actual
    // committed parent is current.component via root.child/component.child.
    assert.equal(current.root.child,current.component);
    assert.equal(current.component.child,current.fiber);
  }});
  assert.equal(value.email,'current@example.invalid');
});
