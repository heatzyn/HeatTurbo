const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => [...document.querySelectorAll(selector)];
function toast(message, error = false) { const n=$('#toast'); n.textContent=message; n.className=`toast show${error?' error':''}`; setTimeout(()=>n.className='toast',3200); }
async function request(url, options) { const r=await fetch(url,options); const d=await r.json(); if(!r.ok) throw new Error(d.message||'Não foi possível concluir a operação.'); return d; }
function fillSystem(s) {
  $('#cpu-name').textContent=s.cpu; $('#gpu-name').textContent=s.gpu; $('#ram-name').textContent=s.ram; $('#disk-name').textContent=s.disk;
  $('#disk-model').textContent=s.diskModel; $('#os-name').textContent=s.operatingSystem; $('#os-version').textContent=s.version; $('#uptime').textContent=s.uptime;
  $('#computer-name').textContent=s.computerName; $('#machine-name').textContent=s.computerName;
  $('#platform-status').textContent=s.isWindows?'Windows detectado · pronto':'Prévia — execute no Windows';
  $('#updated-at').textContent=`Atualizado às ${new Date(s.capturedAt).toLocaleTimeString('pt-BR',{hour:'2-digit',minute:'2-digit'})}`;
}
async function loadSystem(refresh=false) { try { fillSystem(await request(`/api/system${refresh?'?refresh=true':''}`)); } catch(e){ toast(e.message,true); } }
function renderOptimizations(items) {
  $('#active-count').textContent=items.filter(x=>x.isActive).length;
  $('#optimization-list').innerHTML=items.map(item=>`<article class="optimization-item ${item.isActive?'enabled':''}"><div class="opt-mark">${item.isActive?'✓':'⚡'}</div><div class="opt-copy"><div><span>${item.category}</span>${item.requiresRestart?'<small>REINÍCIO</small>':''}</div><h3>${item.name}</h3><p>${item.description}</p></div><button class="toggle ${item.isActive?'on':''}" data-id="${item.id}" data-active="${item.isActive}" aria-label="${item.isActive?'Restaurar':'Ativar'} ${item.name}"><i></i></button></article>`).join('');
}
async function loadOptimizations(){ try{renderOptimizations(await request('/api/optimizations'));}catch(e){toast(e.message,true);} }
async function runAnalysis(){ const b=$('#analyze-btn'); b.disabled=true;b.textContent='Analisando...';try{const d=await request('/api/analyze',{method:'POST'});fillSystem(d.system);$('#health-score').textContent=d.score;$('#score-ring').style.setProperty('--score',`${d.score*3.6}deg`);$('#score-message').textContent=`${d.active} de ${d.available} ajustes recomendados ativos`;await loadOptimizations();toast('Análise concluída. Nenhuma alteração foi aplicada.');}catch(e){toast(e.message,true);}finally{b.disabled=false;b.innerHTML='Analisar novamente <span>→</span>';}}
$$('.nav-item').forEach(b=>b.addEventListener('click',()=>{$$('.nav-item, .view').forEach(x=>x.classList.remove('active'));b.classList.add('active');$(`#${b.dataset.view}`).classList.add('active');$('#page-title').textContent=b.textContent.trim();}));
$('#optimization-list').addEventListener('click',async e=>{const b=e.target.closest('.toggle');if(!b||b.disabled)return;b.disabled=true;const action=b.dataset.active==='true'?'restore':'apply';try{const r=await request(`/api/optimizations/${b.dataset.id}/${action}`,{method:'POST'});toast(r.message);await loadOptimizations();}catch(err){toast(err.message,true);b.disabled=false;}});
$('#analyze-btn').addEventListener('click',runAnalysis); $('#refresh-btn').addEventListener('click',()=>loadSystem(true)); loadSystem(); loadOptimizations();
