namespace UnityDecompiler.Server;

/// <summary>The web UI, inlined so the server runs as one self-contained file with no wwwroot to ship.</summary>
public static class UiPages
{
    private const string Style = """
        :root{
          --bg:#0e0f13; --panel:#171922; --panel-2:#1e2130; --border:#2a2e3e;
          --text:#e7e9f0; --muted:#9aa0b4; --accent:#6aa6ff; --accent-2:#8b7bff;
          --good:#5ac77a; --warn:#e6be5a; --bad:#ef6b6b; --radius:14px;
        }
        *{box-sizing:border-box}
        html,body{margin:0;height:100%}
        body{background:radial-gradient(1200px 600px at 70% -10%,#1a1d2e 0,var(--bg) 55%);
          color:var(--text);font:15px/1.5 "Segoe UI",system-ui,-apple-system,Roboto,Arial,sans-serif;
          display:flex;flex-direction:column;align-items:center;padding:28px 16px;min-height:100%}
        a{color:var(--accent)}
        .wrap{width:100%;max-width:760px}
        header{display:flex;align-items:center;gap:14px;margin-bottom:6px}
        .logo{width:42px;height:42px;border-radius:11px;flex:0 0 auto;
          background:linear-gradient(135deg,var(--accent),var(--accent-2));
          display:flex;align-items:center;justify-content:center;font-size:22px}
        h1{font-size:22px;margin:0;font-weight:650}
        .sub{color:var(--muted);font-size:13.5px;margin:2px 0 22px}
        .card{background:var(--panel);border:1px solid var(--border);border-radius:var(--radius);padding:22px}
        .drop{border:2px dashed #3a4057;border-radius:var(--radius);padding:40px 20px;text-align:center;
          cursor:pointer;transition:.15s;background:var(--panel-2)}
        .drop:hover,.drop.hot{border-color:var(--accent);background:#20263c}
        .drop .big{font-size:17px;font-weight:600;margin:10px 0 4px}
        .drop .small{color:var(--muted);font-size:13px}
        .arrow{font-size:30px;opacity:.75}
        .row{display:flex;gap:16px;flex-wrap:wrap;margin-top:14px;color:var(--muted);font-size:13px}
        .btn{appearance:none;border:1px solid var(--border);background:var(--panel-2);color:var(--text);
          padding:9px 16px;border-radius:10px;cursor:pointer;font-size:14px;transition:.15s}
        .btn:hover{border-color:var(--accent)}
        .btn.primary{background:linear-gradient(135deg,var(--accent),var(--accent-2));border:none;color:#0b0d14;font-weight:650}
        .btn:disabled{opacity:.5;cursor:default}
        .hidden{display:none!important}
        .bar{height:10px;background:var(--panel-2);border-radius:99px;overflow:hidden;margin:16px 0 8px;border:1px solid var(--border)}
        .bar>i{display:block;height:100%;width:0;border-radius:99px;
          background:linear-gradient(90deg,var(--accent),var(--accent-2));transition:width .3s}
        .status{font-size:14px;color:var(--muted)}
        .log{margin-top:16px;background:#0b0d13;border:1px solid var(--border);border-radius:10px;
          padding:12px 14px;max-height:280px;overflow:auto;font:12.5px/1.55 ui-monospace,Consolas,monospace;white-space:pre-wrap}
        .log .info{color:#c4c9db}.log .success{color:var(--good)}.log .warning{color:var(--warn)}.log .error{color:var(--bad)}
        .meta{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin:4px 0 2px}
        .tile{background:var(--panel-2);border:1px solid var(--border);border-radius:10px;padding:12px 14px}
        .tile .k{color:var(--muted);font-size:12px}.tile .v{font-size:16px;font-weight:600;margin-top:3px}
        .alert{border-radius:10px;padding:12px 14px;margin-top:14px;font-size:14px}
        .alert.bad{background:#3a1d20;border:1px solid #5e2a2e;color:#ffd4d4}
        .alert.warn{background:#3a3320;border:1px solid #5e522a;color:#ffeec2}
        .alert.good{background:#1d3a26;border:1px solid #2a5e3a;color:#c8f3d5}
        .center{text-align:center}
        .foot{color:var(--muted);font-size:12px;margin-top:20px;text-align:center}
        input[type=password]{background:var(--panel-2);border:1px solid var(--border);color:var(--text);
          padding:10px 12px;border-radius:10px;font-size:15px;width:100%}
        label{font-size:13px;color:var(--muted)}
        """;

    public static readonly string Index = $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Unity Decompiler</title><style>{{Style}}</style></head>
        <body><div class="wrap">
          <header><div class="logo">⚙️</div><div>
            <h1>Unity Decompiler</h1>
            <div class="sub">Upload a Unity game, get its C# source back — with a credential scan.</div>
          </div></header>

          <div class="card" id="uploadCard">
            <div class="drop" id="drop">
              <div class="arrow">⬇️</div>
              <div class="big">Drop a game .zip here</div>
              <div class="small" id="hint">or click to choose a file · zip the game folder (the one with &lt;Game&gt;_Data) · a single Assembly-CSharp.dll works too</div>
              <input type="file" id="file" accept=".zip,.dll" class="hidden">
            </div>
            <div class="row">
              <span id="limit"></span>
              <span>· Files are deleted automatically after processing.</span>
            </div>
          </div>

          <div class="card hidden" id="progressCard">
            <div class="status" id="status">Uploading…</div>
            <div class="bar"><i id="barFill"></i></div>
            <div class="log hidden" id="log"></div>
          </div>

          <div class="card hidden" id="resultCard">
            <div class="meta" id="meta"></div>
            <div id="secretAlert"></div>
            <div class="center" style="margin-top:18px">
              <a class="btn primary" id="download" href="#">⬇ Download decompiled source (.zip)</a>
              <button class="btn" id="again" style="margin-left:8px">Decompile another</button>
            </div>
          </div>

          <div class="foot">
            Only decompile games you own or have permission to study. Mod menus are for single-player / offline use —
            don't use them online. IL2CPP games can't be turned back into C#.
          </div>
        </div>
        <script>{{Script}}</script>
        </body></html>
        """;

    public static string Login(bool enabled) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Sign in · Unity Decompiler</title><style>{{Style}}</style></head>
        <body><div class="wrap" style="max-width:380px">
          <header><div class="logo">⚙️</div><div><h1>Unity Decompiler</h1>
          <div class="sub">This instance is password protected.</div></div></header>
          <div class="card">
            <form id="f">
              <label for="p">Password</label>
              <input type="password" id="p" autofocus style="margin:8px 0 14px">
              <button class="btn primary" style="width:100%">Sign in</button>
              <div class="alert bad hidden" id="err" style="margin-top:12px"></div>
            </form>
          </div>
        </div>
        <script>
        const f=document.getElementById('f'),err=document.getElementById('err');
        f.onsubmit=async e=>{
          e.preventDefault();
          const b=new FormData();b.append('password',document.getElementById('p').value);
          const r=await fetch('/api/login',{method:'POST',body:b});
          if(r.ok){location.href='/';}else{err.textContent='Wrong password.';err.classList.remove('hidden');}
        };
        </script>
        </body></html>
        """;

    private const string Script = """
        const $=id=>document.getElementById(id);
        const drop=$('drop'),file=$('file');
        let timer=null;

        fetch('/api/config').then(r=>r.json()).then(c=>{
          $('limit').textContent='Max upload: '+c.maxUploadMb+' MB';
        }).catch(()=>{});

        drop.onclick=()=>file.click();
        drop.ondragover=e=>{e.preventDefault();drop.classList.add('hot');};
        drop.ondragleave=()=>drop.classList.remove('hot');
        drop.ondrop=e=>{e.preventDefault();drop.classList.remove('hot');
          if(e.dataTransfer.files.length)upload(e.dataTransfer.files[0]);};
        file.onchange=()=>{if(file.files.length)upload(file.files[0]);};
        $('again').onclick=()=>{clearInterval(timer);
          $('resultCard').classList.add('hidden');$('progressCard').classList.add('hidden');
          $('uploadCard').classList.remove('hidden');file.value='';};

        function show(card){['uploadCard','progressCard','resultCard'].forEach(id=>
          $(id).classList.toggle('hidden',id!==card));}
        function setBar(f){$('barFill').style.width=Math.round((f||0)*100)+'%';}

        async function upload(f){
          const ext=f.name.toLowerCase().split('.').pop();
          if(ext!=='zip'&&ext!=='dll'){alert('Please upload a .zip of the game folder, or a single .dll.');return;}
          show('progressCard');$('status').textContent='Uploading '+f.name+'…';setBar(0);
          $('log').classList.add('hidden');$('log').textContent='';
          const body=new FormData();body.append('game',f);
          let res;
          try{res=await fetch('/api/jobs',{method:'POST',body});}
          catch{fail('Upload failed. Is the file too large?');return;}
          const data=await res.json().catch(()=>({}));
          if(!res.ok){fail(data.error||'Upload was rejected.');return;}
          poll(data.id);
        }

        function fail(msg){show('progressCard');$('status').innerHTML=
          '<span style="color:var(--bad)">'+esc(msg)+'</span>';setBar(0);
          const a=document.createElement('button');a.className='btn';a.textContent='Try again';
          a.style.marginTop='14px';a.onclick=()=>show('uploadCard');
          $('progressCard').appendChild(a);}

        function poll(id){
          clearInterval(timer);
          timer=setInterval(async()=>{
            let r;try{r=await fetch('/api/jobs/'+id);}catch{return;}
            if(!r.ok){clearInterval(timer);fail('Lost track of the job (it may have expired).');return;}
            const j=await r.json();
            setBar(j.progress);
            const last=j.log[j.log.length-1];
            $('status').textContent=last?last.Message:(j.state==='queued'?'Waiting in queue…':'Working…');
            const log=$('log');log.classList.remove('hidden');
            log.innerHTML=j.log.map(l=>'<div class="'+esc(l.Level)+'">'+esc(l.Message)+'</div>').join('');
            log.scrollTop=log.scrollHeight;
            if(j.state==='done'){clearInterval(timer);done(id,j);}
            else if(j.state==='failed'){clearInterval(timer);fail(j.error||'Decompiling failed.');}
          },900);
        }

        function done(id,j){
          show('resultCard');
          const mb=(j.downloadSize/1048576).toFixed(1);
          $('meta').innerHTML=[
            tile('Game',j.game||j.input),
            tile('Engine',j.backend+(j.unityVersion?' · Unity '+j.unityVersion:'')),
            tile('Download',mb+' MB'),
          ].join('');
          const a=$('secretAlert');
          if(j.secretCount>0){
            const cls=j.highSecretCount>0?'bad':'warn';
            a.className='alert '+cls;
            a.innerHTML='🔑 Found <b>'+j.secretCount+'</b> possible hardcoded secret'+
              (j.secretCount===1?'':'s')+(j.highSecretCount>0?' ('+j.highSecretCount+' high severity)':'')+
              '. See <b>SECURITY-SCAN.txt</b> in the download. If any are real, rotate them.';
          }else{a.className='alert good';a.innerHTML='✓ No hardcoded credentials found in the decompiled code.';}
          const dl=$('download');dl.href='/api/jobs/'+id+'/download';
        }

        function tile(k,v){return '<div class="tile"><div class="k">'+esc(k)+
          '</div><div class="v">'+esc(v)+'</div></div>';}
        function esc(s){return String(s==null?'':s).replace(/[&<>"]/g,c=>
          ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));}
        """;
}
