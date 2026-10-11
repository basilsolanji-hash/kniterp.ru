/* knitERP Книт + Ника, v2.0.0. No dependencies. Images are served by the host app.
   knitERP D116: атрибут texts (JSON) — переводы подписей и состояний на язык интерфейса; без него — русские тексты пакета.
   Движение фигуры — из пакета анимаций 1.0 (knit-idle: 4,8 с, ±0,18°, до 100,6 %, опора у ног) вместо прежнего breathe. */
(() => {
  'use strict';
  if (customElements.get('knit-assistant')) return;
  const scriptBase = new URL('../assets/', document.currentScript?.src || location.href).href;
  const states = Object.freeze({
    welcome: ['Здравствуйте! Я Книт.', 'Помогу разобраться с возможностями knitERP.', 'Открыть помощь'],
    thinking: ['Обрабатываю запрос', 'Это может занять немного времени.', 'Обработка…'],
    success: ['Готово', 'Операция выполнена.', 'Продолжить'],
    warning: ['Нужно ваше внимание', 'Проверьте данные перед продолжением.', 'Проверить'],
    error: ['Не удалось выполнить', 'Попробуйте ещё раз или откройте помощь.', 'Повторить'],
    offline: ['Нет соединения', 'Проверьте подключение к сети.', 'Повторить']
  });
  class KnitAssistant extends HTMLElement {
    static get observedAttributes(){return ['image-src','persona','texts']}
    _t(){if(this._textsRaw!==this.getAttribute('texts')){this._textsRaw=this.getAttribute('texts');try{this._texts=JSON.parse(this._textsRaw||'{}')||{}}catch{this._texts={}}}return this._texts||{}}
    _fmt(key,fallback){const v=this._t()[key];return (typeof v==='string'&&v?v:fallback).replace('{name}',this.personaName)}
    constructor() {
      super(); this.attachShadow({mode:'open'}); this._state='welcome'; this._open=false;
      this.shadowRoot.innerHTML = `
      <style>
      :host{--knit-accent:#30d5c8;--knit-bg:#252629;--knit-text:#fff;--knit-muted:#c7cbd1;--knit-border:#44464b;--knit-right:20px;--knit-bottom:20px;--knit-z:1000;position:fixed;right:calc(var(--knit-right) + env(safe-area-inset-right,0px));bottom:calc(var(--knit-bottom) + env(safe-area-inset-bottom,0px));z-index:var(--knit-z);font:14px/1.5 system-ui,-apple-system,'Segoe UI',sans-serif;color:var(--knit-text);color-scheme:dark}
      :host([theme=light]){--knit-bg:#fff;--knit-text:#202126;--knit-muted:#535862;--knit-border:#d6dae0;color-scheme:light}
      *{box-sizing:border-box} [hidden]{display:none!important} button{font:inherit;cursor:pointer}button:focus-visible{outline:3px solid var(--knit-accent);outline-offset:3px}button:disabled{cursor:wait;opacity:.65}
      .launcher{position:relative;width:64px;height:64px;border:1px solid var(--knit-border);border-radius:20px;background:var(--knit-bg);box-shadow:0 7px 24px #0003;overflow:hidden;display:block;margin-left:auto;color:var(--knit-text)}
      .avatar{position:absolute;width:220px;height:auto;max-width:none;left:-78px;top:-5px;pointer-events:none}
      .fallback{display:block;font-weight:700;font-size:28px;color:var(--knit-accent)}
      .panel{position:relative;width:360px;max-width:calc(100vw - 40px - env(safe-area-inset-right,0px) - env(safe-area-inset-left,0px));max-height:calc(100vh - 125px);max-height:calc(100dvh - 125px);overflow:auto;margin-bottom:12px;padding:20px;background:var(--knit-bg);border:1px solid var(--knit-border);border-radius:22px;box-shadow:0 16px 50px #0003;animation:enter .22s ease-out}
      header{display:flex;justify-content:space-between;align-items:flex-start;gap:10px}strong{font-size:16px}.caption{font-size:12px;color:var(--knit-muted)}.close{flex-shrink:0;width:44px;height:44px;border:0;border-radius:10px;background:transparent;color:var(--knit-text);font-size:24px;line-height:1}
      .content{display:grid;grid-template-columns:100px 1fr;gap:14px;align-items:center;margin:12px 0 18px}.mascot{width:100%;height:150px;object-fit:contain;transform-origin:50% 97.222%}:host([animated]) .mascot{animation:knit-idle 4.8s ease-in-out infinite}
      h2{font-size:15px;line-height:1.4;margin:0 0 7px;font-weight:650}p{margin:0;color:var(--knit-muted);overflow-wrap:anywhere}.status{height:8px;width:8px;border-radius:50%;display:inline-block;background:var(--knit-accent);margin-right:6px}.status[data-state=warning]{background:#e2ae51}.status[data-state=error]{background:#f48585}.status[data-state=offline]{background:#a6aab2}.status[data-state=thinking]{animation:pulse 1.3s ease-in-out infinite}
      .action{width:100%;min-height:44px;padding:11px 16px;background:var(--knit-accent);color:#102a28;font-weight:650;border:0;border-radius:12px}
      @keyframes enter{from{opacity:0;transform:translateY(7px)}to{opacity:1;transform:translateY(0)}}@keyframes knit-idle{0%,100%{transform:rotate(0) scale(1)}25%{transform:rotate(.18deg) scale(1.003)}50%{transform:rotate(0) scale(1.006)}75%{transform:rotate(-.18deg) scale(1.003)}}@keyframes pulse{50%{opacity:.4}}
      @media(max-width:420px){:host{--knit-right:12px;--knit-bottom:12px}.panel{width:340px;max-width:calc(100vw - 24px - env(safe-area-inset-right,0px) - env(safe-area-inset-left,0px));padding:16px}.content{grid-template-columns:85px 1fr;gap:10px}.mascot{height:135px}}
      @media(forced-colors:active){.launcher,.panel,.action,.close{border:1px solid ButtonText}.status{background:CanvasText}button:focus-visible{outline-color:Highlight}}
      @media(prefers-reduced-motion:reduce){*,*::before,*::after{animation:none!important;transition:none!important}}
      </style>
      <section class="panel" id="panel" aria-labelledby="name" hidden>
        <header><div><strong id="name">Книт</strong><div class="caption">Помощник knitERP</div></div><button class="close" type="button" aria-label="Свернуть помощника">×</button></header>
        <div class="content"><img class="mascot" alt="Вязаный помощник Книт в деловом костюме" width="100" height="150" decoding="async"><div aria-live="polite" aria-atomic="true"><h2><span class="status" aria-hidden="true"></span><span class="title"></span></h2><p class="message"></p></div></div>
        <button class="action" type="button"></button>
      </section>
      <button class="launcher" type="button" aria-label="Открыть помощника Книта" aria-controls="panel" aria-expanded="false"><span class="fallback" aria-hidden="true">K</span><img class="avatar" alt="" width="220" height="330" decoding="async"></button>`;
      this.$ = sel => this.shadowRoot.querySelector(sel);
      this.$('.launcher').addEventListener('click',()=>this._open?this.close():this.open());
      this.$('.close').addEventListener('click',()=>this.close());
      this.shadowRoot.addEventListener('keydown',e=>{if(e.key==='Escape'&&this._open){e.preventDefault();this.close()}});
      this.$('.action').addEventListener('click',()=>this.dispatchEvent(new CustomEvent('knit:action',{bubbles:true,composed:true,detail:{state:this._state,action:({welcome:'help',success:'continue',warning:'review',error:'retry',offline:'retry'})[this._state]||'help'}})));
      this.shadowRoot.querySelectorAll('img').forEach(img=>{
        img.addEventListener('error',()=>{img.hidden=true;if(img.classList.contains('avatar'))this.$('.fallback').hidden=false});
        img.addEventListener('load',()=>{img.hidden=false;if(img.classList.contains('avatar'))this.$('.fallback').hidden=true});
      });
    }
    get persona(){return this.getAttribute('persona')==='nika'?'nika':'knit'}
    get personaName(){return this.persona==='nika'?'Ника':'Книт'}
    _syncPersona(){
      this.$('#name').textContent=this.personaName;
      this.$('.caption').textContent=this.persona==='nika'?this._fmt('captionNika','Помощница knitERP'):this._fmt('captionKnit','Помощник knitERP');
      this.$('.mascot').alt=this._fmt('alt','{name} в деловом костюме с планшетом');
      this.$('.close').setAttribute('aria-label',this._fmt('collapse','Свернуть: {name}'));
      this.$('.launcher').setAttribute('aria-label',this._open?this._fmt('collapse','Свернуть: {name}'):this._fmt('open','Открыть: {name}'));
      if(this._state)this.setState(this._state,this._message);
    }
    attributeChangedCallback(){if(this.isConnected&&this._initialized){this._syncPersona();this._updateImage()}}
    _updateImage(){
      const raw=this.getAttribute('image-src')||new URL(this.persona+'-business-512.webp',scriptBase).href;
      let url;try{url=new URL(raw,document.baseURI);if(!['http:','https:','file:','data:','blob:'].includes(url.protocol))throw Error('Invalid image URL')}catch{url=null}
      this.$('.fallback').hidden=false;
      this.shadowRoot.querySelectorAll('img').forEach(img=>{img.hidden=true;if(url){img.src=url.href;if(img.complete&&img.naturalWidth){img.hidden=false;if(img.classList.contains('avatar'))this.$('.fallback').hidden=true}}else img.removeAttribute('src')});
    }
    connectedCallback(){
      this._syncPersona();this._updateImage();
      if(!this._initialized){this._initialized=true;this.setState('welcome');
        if(this.hasAttribute('auto-open')){let seen=false;try{seen=sessionStorage.getItem('knit-welcome-v1')==='1'}catch{}if(!seen){this.open({focus:false});try{sessionStorage.setItem('knit-welcome-v1','1')}catch{}}}
      }
    }
    setState(state,message){
      if(!Object.prototype.hasOwnProperty.call(states,state))throw new TypeError('Unknown knit state: '+state);
      this._state=state;this._message=message;const [title,defaultMessage,action]=states[state];const tr=(this._t().states||{})[state]||[];
      this.$('.title').textContent=state==='welcome'?this._fmt('hello','Здравствуйте! Я {name}.'):(tr[0]||title);this.$('.message').textContent=message==null?(tr[1]||defaultMessage):String(message);
      this.$('.action').textContent=tr[2]||action;this.$('.action').disabled=state==='thinking';this.$('.status').dataset.state=state;
      this.setAttribute('data-state',state);return this;
    }
    open({focus=true}={}){this._open=true;this.$('.panel').hidden=false;this.$('.launcher').setAttribute('aria-expanded','true');this.$('.launcher').setAttribute('aria-label',this._fmt('collapse','Свернуть: {name}'));if(focus)this.$('.close').focus();return this}
    close(){const restoreFocus=!!this.shadowRoot.activeElement;this._open=false;this.$('.panel').hidden=true;this.$('.launcher').setAttribute('aria-expanded','false');this.$('.launcher').setAttribute('aria-label',this._fmt('open','Открыть: {name}'));if(restoreFocus)this.$('.launcher').focus();return this}
    get state(){return this._state}get isOpen(){return this._open}
  }
  customElements.define('knit-assistant',KnitAssistant);
})();
