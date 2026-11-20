namespace VPSnode.Core;

// Executed through SSH in memory. Every database connection is read-only; no API mutations.
internal static class InventoryScript
{
    internal const string Python = """
        import json, os, platform, re, sqlite3, subprocess, socket, glob
        errors = []
        def command(*args):
            try: return subprocess.check_output(args, stderr=subprocess.DEVNULL, text=True, timeout=4)
            except (OSError, subprocess.SubprocessError) as e:
                errors.append(args[0]+': '+type(e).__name__); return ''
        ports = []
        listener_errors = len(errors)
        listeners = command('ss','-H','-lntup')
        listeners_available = len(errors) == listener_errors
        for line in listeners.splitlines():
            cols = line.split(None, 6)
            if len(cols) < 6: continue
            address, sep, port = cols[4].rpartition(':')
            if sep and port.isdigit():
                found = re.search(r'"([^"]+)"', cols[6]) if len(cols)>6 else None
                ports.append({'protocol':cols[0].upper(),'address':address,'port':int(port),'service':found.group(1) if found else ''})
        memory = 0
        try: memory = int(next(x for x in open('/proc/meminfo') if x.startswith('MemTotal')).split()[1]) / 1048576
        except (OSError, ValueError, StopIteration) as e: errors.append('memory: '+type(e).__name__)
        disk = os.statvfs('/')
        hardware = str(os.cpu_count() or 0)+' vCPU · '+str(round(memory,1))+' GiB RAM · '+str(round(disk.f_blocks*disk.f_frsize/1073741824,1))+' GiB / · '+platform.machine()
        os_name = platform.platform()
        try:
            for line in open('/etc/os-release'):
                if line.startswith('PRETTY_NAME='): os_name=line.split('=',1)[1].strip().strip('"'); break
        except OSError as e: errors.append('os-release: '+type(e).__name__)
        # A process's startup file is evidence, not a dump of its live in-memory configuration.
        generated = []; config_paths = []; processes = []
        for proc in glob.glob('/proc/[0-9]*/cmdline'):
            try:
                args = [os.fsdecode(a) for a in open(proc,'rb').read().split(b'\0') if a]
                if not args or not os.path.basename(args[0]).startswith('xray'): continue
                pid=proc.split('/')[2]; processes.append(pid)
                for index,a in enumerate(args[:-1]):
                    if a in ('-c','-config','--config'):
                        p=args[index+1]
                        if not os.path.isabs(p): p=os.path.join(os.readlink('/proc/'+pid+'/cwd'),p)
                        config_paths.append(os.path.normpath(p))
            except (OSError, IndexError): continue
        if not config_paths: config_paths=['/usr/local/x-ui/bin/config.json']
        for path in sorted(set(config_paths)):
            try:
                with open(path) as f: config=json.load(f)
                for inbound in config.get('inbounds',[]): generated.append(dict(inbound, sourcePath=path))
            except (OSError, ValueError, TypeError, AttributeError) as e: errors.append('generated config: '+type(e).__name__)
        inbounds=[]; panel={}; denied=True; db_path=''
        for path in ['/etc/x-ui/x-ui.db','/usr/local/x-ui/bin/x-ui.db']:
            if not os.path.exists(path): continue
            try:
                with sqlite3.connect('file:'+path+'?mode=ro',uri=True,timeout=2.0) as db:
                    db.row_factory=sqlite3.Row
                    db.execute('BEGIN')
                    tables={r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                    try: panel=dict(db.execute("SELECT key,value FROM settings WHERE key IN ('webPort','webBasePath','webCertFile','webKeyFile')"))
                    except sqlite3.Error as e: errors.append('panel settings: '+type(e).__name__)
                    for row in db.execute('SELECT * FROM inbounds'):
                        d=dict(row); ident=d['id']; issues=[]
                        def parse(raw,label):
                            try:
                                result=json.loads(raw or '{}')
                                if not isinstance(result,dict): raise ValueError()
                                return result
                            except (ValueError, TypeError): issues.append(label+': invalid JSON'); return {}
                        settings=parse(d.get('settings'),'settings'); stream=parse(d.get('stream_settings'),'stream')
                        clients=settings.get('clients',[]); client_source='inbounds.settings.clients'
                        if 'clients' in tables and 'client_inbounds' in tables:
                            try:
                                clients=[]
                                for row in db.execute('SELECT c.*,ci.flow_override FROM clients c JOIN client_inbounds ci ON ci.client_id=c.id WHERE ci.inbound_id=?',(ident,)):
                                    c=dict(row)
                                    clients.append(dict(c, id=c.get('uuid') or c.get('auth') or c.get('password') or '', flow=c.get('flow_override') if c.get('flow_override') is not None else c.get('flow','')))
                                client_source='clients + client_inbounds'
                            except sqlite3.Error as e: issues.append('clients: '+type(e).__name__);clients=[]
                        if not isinstance(clients,list): clients=[];issues.append('clients: invalid array')
                        settings['clients']=clients
                        if 'inbound_fallbacks' in tables:
                            try:
                                fallback=[{k:r[k] for k in ['name','alpn','path','dest','xver']} for r in db.execute('SELECT * FROM inbound_fallbacks WHERE master_id=? ORDER BY sort_order,id',(ident,))]
                                if fallback or 'fallbacks' in settings: settings['fallbacks']=fallback
                            except sqlite3.Error as e: issues.append('fallbacks: '+type(e).__name__)
                        reality=stream.get('realitySettings') or {}
                        matching=[g for g in generated if g.get('port')==d['port'] and g.get('protocol')==d['protocol']]
                        inbounds.append({'id':str(ident),'name':d.get('remark') or d['protocol']+':'+str(d['port']),'enabled':bool(d['enable']),
                            'listen':d.get('listen') or '*','port':d['port'],'protocol':d['protocol'],'target':str(reality.get('target') or reality.get('dest') or ''),
                            'serverNames':','.join(str(x) for x in reality.get('serverNames',[])), 'transport':stream.get('network',''),'security':stream.get('security',''),
                            'settings':settings,'stream':stream,'clients':clients,'clientSource':client_source,'shareAddress':d.get('share_addr') or '',
                            'shareStrategy':d.get('share_addr_strategy') or '', 'generated':matching[0] if len(matching)==1 else None,'issues':issues})
                    db_path=path;denied=False;break
            except (OSError, sqlite3.Error, KeyError, TypeError, AttributeError) as e: errors.append('database '+path+': '+type(e).__name__)
        print(json.dumps({'hostname':socket.gethostname(),'hardware':hardware,'os':os_name,'ports':ports,'listenersAvailable':listeners_available,
            'inbounds':inbounds,'inboundsUnavailable':denied,'databasePath':db_path,'processes':processes,'errors':errors,
            'panelPort':int(panel['webPort']) if str(panel.get('webPort','')).isdigit() else None,'panelPath':panel.get('webBasePath') or '',
            'panelScheme':('https' if panel.get('webCertFile') and panel.get('webKeyFile') else 'http') if panel else ''}))
        """;
}
