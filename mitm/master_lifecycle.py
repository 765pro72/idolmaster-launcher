"""Verified public master publication and a single, client-confirmed rollback slot.

No networking, account identities, tokens or game process injection. Version hashes
are opaque: file modification times never establish which master version is newer.
"""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import os
import re
import shutil
import threading
from urllib.parse import urlsplit

def digest(data):return hashlib.sha256(data).hexdigest()
def stamp():return datetime.now(timezone.utc).isoformat()
def timestamp(text):
    try:return datetime.fromisoformat(str(text).replace('Z','+00:00')).timestamp()
    except (ValueError,TypeError):return 0
def json_bytes(value):return (json.dumps(value,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
def atomic(path,data):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_name(path.name+'.master-tmp');temporary.write_bytes(data);os.replace(temporary,path)
def cache_name(pack):
    return hashlib.md5(pack['type'].encode()).hexdigest().upper()+'_'+hashlib.md5(pack['url'].encode()).hexdigest().upper()

def describe(raw,wire):
    plain=wire.decrypt_body(raw)
    if plain is None:raise ValueError('master响应无法解密')
    top=list(wire.fields(plain));tags=[v for f,t,r,l,v in top if f==1 and t==2]
    if len(tags)!=1 or any(f not in (1,2) for f,t,r,l,v in top):raise ValueError('master响应结构不兼容')
    tag=tags[0];rows=list(wire.fields(tag));versions=[v.decode('ascii') for f,t,r,l,v in rows if f==1 and t==2]
    if len(versions)!=1 or not re.fullmatch('[0-9a-fA-F]{64}',versions[0]):raise ValueError('master版本字段无效')
    packs=[];names=set()
    for f,t,r,l,v in rows:
        if f!=2 or t!=2:continue
        fields={a:(x if b==2 else n) for a,b,q,n,x in wire.fields(v)}
        name=fields[1].decode();filename=fields[2].decode('ascii');key=bytes.fromhex(fields[4].decode('ascii'));url=fields[5].decode()
        size=fields[3];parsed=urlsplit(url)
        if (not re.fullmatch('[A-Za-z][A-Za-z0-9_]*',name) or name in names or
            not re.fullmatch('[0-9a-fA-F]{64}',filename) or len(key)!=32 or
            not isinstance(size,int) or size<=0 or size%4096 or size>512*1024*1024 or
            parsed.scheme!='https' or parsed.hostname!='stat.game-gakuen-idolmaster.jp' or parsed.query or parsed.fragment or
            parsed.path!='/master/'+name+'/'+filename):raise ValueError('master表描述校验失败：'+name)
        names.add(name);packs.append({'type':name,'filename':filename,'key':key,'url':url,'size':size})
    if len(packs)<100:raise ValueError('master表数量不足100，拒绝替换现役清单')
    return {'raw':raw,'version':versions[0].lower(),'tag_sha256':digest(tag),'response_sha256':digest(raw),
            'proto_hash':next((v for f,t,r,l,v in top if f==2 and t==2),b''),'packs':packs,'observed_at':0}

def metadata(candidate):
    return {'format':2,'master_tag_version':candidate['version'],'pack_count':len(candidate['packs']),
            'master_tag_sha256':candidate['tag_sha256'],'response_sha256':candidate['response_sha256'],
            'published_at':stamp(),'observed_at':candidate.get('observed_at',0),
            'public_version_manifest_only':True,'contains_request_or_headers':False}

def current(root,wire):
    root=Path(root);item=describe((root/'mitm/offline-fallbacks/Master_Get.bin').read_bytes(),wire)
    path=root/'mitm/bootstrap-master.json'
    data=json.loads(path.read_text('utf-8')) if path.is_file() else {}
    if data and (data['response_sha256']!=item['response_sha256'] or data['master_tag_version']!=item['version']):raise ValueError('现役master与版本元数据不一致，已停止')
    item['reference_time']=max(timestamp(data.get('published_at')),float(data.get('observed_at') or 0))
    return item

def candidates(root,wire):
    active=current(root,wire);result={active['tag_sha256']:active}
    mitm=Path(root)/'mitm'
    for file in list((mitm/'records').glob('*Master_Get*.json'))+list((mitm/'cache/client.api.Master_Get').glob('*.json')):
        try:
            obj=json.loads(file.read_text('utf-8-sig'))
            if obj.get('path')!='/client.api.Master/Get':continue
            item=describe(bytes.fromhex(obj['resp_body_hex']),wire)
            # Captures are written only for upstream responses; local replays do
            # not acquire a new observation time by copying/touching a file.
            observed=timestamp(obj.get('ts')) if obj.get('source')=='UPSTREAM' else timestamp(obj.get('cached_at'))
            item['observed_at']=observed
            previous=result.get(item['tag_sha256'])
            if previous is None or observed>previous.get('observed_at',0):result[item['tag_sha256']]=item
        except (ValueError,KeyError,TypeError,OSError,UnicodeError):continue
    return active,list(result.values())

def select(root,cache,wire,validator):
    active,options=candidates(root,wire);cache=Path(cache).resolve();matched=[];failures={};validated={}
    for item in options:
        checks={};error=None
        for pack in item['packs']:
            path=cache/cache_name(pack)
            if not path.is_file() or path.is_symlink() or path.stat().st_size!=pack['size']:
                error=pack['type']+' 缓存缺失或长度不匹配';break
        if error is None:
            for pack in item['packs']:
                path=cache/cache_name(pack);key=(path.name,pack['key'])
                try:
                    if key not in validated:validated[key]=validator.validate(path,pack['key'],pack['type'],pack['size'])
                    checks[path.name]=validated[key]
                except Exception as exc:error=pack['type']+' SQL完整性校验失败（'+type(exc).__name__+'）';break
        if error:failures[item['version']]=error
        else:item['validated_cache']=checks;matched.append(item)
    fresh=[item for item in options if item['version']!=active['version'] and item.get('observed_at',0)>active['reference_time']]
    if fresh:
        latest=max(item['observed_at'] for item in fresh);newest=[item for item in fresh if item['observed_at']==latest]
        if len(newest)!=1:raise ValueError('新master安装记录冲突；现有版本未改动')
        chosen=newest[0]
        if chosen not in matched:raise ValueError('新master尚未完整下载或校验失败：'+failures.get(chosen['version'],'未知原因')+'；现有版本未改动')
    else:
        chosen=next((item for item in matched if item['version']==active['version'] and item['tag_sha256']==active['tag_sha256']),None)
        if chosen is None:raise ValueError('没有与本地完整缓存匹配且可确认的新master清单；请先取得当前版本的公开清单，现有数据未改动')
    ensure_protocol_compatible(root,active,chosen)
    return active,chosen

def pending_dir(root):return Path(root)/'out/local-master-activation'
def read_pending(root):
    path=pending_dir(root)/'state.json'
    return json.loads(path.read_text('utf-8')) if path.is_file() else None
def write_pending(root,state):atomic(pending_dir(root)/'state.json',json_bytes(state))
def controlled(root,relative):
    relative=Path(relative)
    if relative.is_absolute() or '..' in relative.parts or not relative.parts or relative.parts[0]!='mitm':raise ValueError('master事务路径无效')
    base=Path(root).resolve();target=(base/relative).resolve();target.relative_to((base/'mitm').resolve())
    return target
def retire_slot(root):
    path=pending_dir(root)
    if path.is_symlink() or path.resolve().parent!=(Path(root)/'out').resolve():raise ValueError('旧master暂存路径异常，拒绝清理')
    shutil.rmtree(path)

def prepare(root,before,payloads,old,new,cache,validator):
    path=pending_dir(root)
    if path.exists():raise ValueError('上次master仍等待确认或清理，请先进入游戏大厅；不会继续新增备份')
    path.mkdir(parents=True)
    state={'format':1,'status':'preparing','old_version':old['version'],'new_version':new['version'],
           'new_tag_sha256':new['tag_sha256'],'new_response_sha256':new['response_sha256'],
           'cache_root':str(Path(cache).resolve()),'files':[],'obsolete_cache':[],'master_served':False}
    if new.get('local_response_sha256'):
        state['local_response_sha256'] = new['local_response_sha256']
    try:
        for relative,data in before.items():
            relative=str(relative);controlled(root,relative)
            if data is not None:
                saved=path/'before'/relative;saved.parent.mkdir(parents=True,exist_ok=True);saved.write_bytes(data)
            state['files'].append({'path':relative,'before':digest(data) if data is not None else None,'after':digest(payloads[Path(relative)])})
        (path/'previous_master.bin').write_bytes(old['raw'])
        protected={cache_name(p) for p in new['packs']}
        for pack in old['packs']:
            name=cache_name(pack);file=Path(cache)/name
            if name not in protected and file.is_file():
                checked=validator.validate(file,pack['key'],pack['type'],pack['size'])
                state['obsolete_cache'].append({'name':name,'size':checked['size'],'sha256':checked['sha256']})
        write_pending(root,state)
    except Exception:
        # Publication has not begun; only this newly created temporary slot exists.
        retire_slot(root);raise

def recover_preparing(root):
    state=read_pending(root)
    if not state or state['status']!='preparing':return state
    if (Path(root)/'out/local-sync-transaction').exists():return state
    new=True;old=True
    for row in state['files']:
        path=controlled(root,row['path']);actual=digest(path.read_bytes()) if path.is_file() else None
        new &= actual==row['after'];old &= actual==row['before']
    if new:state['status']='waiting_client';write_pending(root,state);return state
    if old:retire_slot(root);return None
    raise ValueError('master更新被中断且文件发生额外变化；旧版暂存仍保留，请恢复后重试')

def cleanup_confirmed(root,wire):
    state=read_pending(root)
    if not state or state['status']!='confirmed':return 0
    active=current(root,wire)
    if active['tag_sha256']!=state['new_tag_sha256']:raise ValueError('现役master已变化，暂停旧版清理')
    old=describe((pending_dir(root)/'previous_master.bin').read_bytes(),wire)
    old_names={cache_name(p) for p in old['packs']};protected={cache_name(p) for p in active['packs']};cache=Path(state['cache_root']).resolve();targets=[]
    for row in state['obsolete_cache']:
        name=row['name']
        if name not in old_names or name in protected or not re.fullmatch('[A-F0-9]{32}_[A-F0-9]{32}',name):raise ValueError('旧master缓存范围校验失败，拒绝清理')
        file=cache/name
        if not file.exists():continue
        if file.is_symlink() or file.resolve().parent!=cache or file.stat().st_size!=row['size'] or digest(file.read_bytes())!=row['sha256']:raise ValueError('旧缓存被其他操作修改，暂停清理')
        targets.append(file)
    for file in targets:file.unlink()
    retired=len(targets);retire_slot(root);return retired

def rollback(root):
    state=read_pending(root)
    if not state:return False
    if state['status']=='confirmed':raise ValueError('新master已经确认生效，不能用此入口回退')
    material=[]
    for row in state['files']:
        target=controlled(root,row['path']);actual=digest(target.read_bytes()) if target.is_file() else None
        if actual not in (row['before'],row['after']):raise ValueError('文件已被其他操作修改，拒绝覆盖：'+row['path'])
        data=None if row['before'] is None else (pending_dir(root)/'before'/row['path']).read_bytes()
        if data is not None and digest(data)!=row['before']:raise ValueError('旧版暂存校验失败，已停止恢复')
        material.append((target,data))
    state['status']='rollback_pending';write_pending(root,state)
    for target,data in material:
        if data is None:target.unlink(missing_ok=True)
        else:atomic(target,data)
    retire_slot(root);return True

class ActiveMaster:
    def __init__(self,root,wire,logger):self.root=Path(root);self.wire=wire;self.log=logger;self.lock=threading.RLock()
    def response(self):
        with self.lock:
            state=read_pending(self.root) if (self.root/'out/local-sync-transaction').exists() else recover_preparing(self.root)
            if state and ((self.root/'out/local-sync-transaction').exists() or state['status'] in ('preparing','rollback_pending')):
                return (pending_dir(self.root)/'previous_master.bin').read_bytes()
            return current(self.root,self.wire)['raw']
    def observe_success(self,path,headers,response):
        with self.lock:
            try:
                state=recover_preparing(self.root)
                if not state:return
                if state['status']=='confirmed':cleanup_confirmed(self.root,self.wire);return
                if state['status']!='waiting_client':return
                if path=='/client.api.Master/Get':
                    item=describe(response,self.wire)
                    if (item['tag_sha256']==state['new_tag_sha256']
                            or item['response_sha256']==state.get('local_response_sha256')):
                        state['master_served']=True
                        state['served_version']=item['version']
                        write_pending(self.root,state)
                        self.log('MASTER activation: new manifest served; previous version retained')
                if path=='/client.api.Home/Enter' and state['master_served']:
                    home=self.wire.decrypt_body(response)
                    rows=list(self.wire.fields(home)) if home is not None else []
                    if any(len([v for f,t,r,l,v in rows if f==number and t==2])!=1 for number in (5,7,9999)):
                        self.log('MASTER activation: invalid Home/Enter structure; previous version retained');return
                    common=next(v for f,t,r,l,v in rows if f==9999 and t==2)
                    if any(len([v for f,t,r,l,v in self.wire.fields(common) if f==number and t==2])!=1 for number in (1,2)):return
                    supplied={str(k).lower():v for k,v in dict(headers).items()}.get('x-master-version')
                    if isinstance(supplied,bytes):supplied=supplied.decode('ascii','replace')
                    if supplied==state.get('served_version', state['new_version']):
                        state['status']='confirmed';write_pending(self.root,state);count=cleanup_confirmed(self.root,self.wire)
                        self.log('MASTER activation: client uses new version; previous slot and',count,'obsolete master cache files cleaned')
                    else:self.log('MASTER activation: waiting for client version confirmation; header_present=',bool(supplied))
            except Exception as exc:self.log('MASTER activation postponed:',str(exc))


def ensure_protocol_compatible(root,active,candidate):
    if candidate['proto_hash']==active['proto_hash']:return
    import importlib.util
    path=Path(__file__).with_name('master_protocol_compatibility.py')
    spec=importlib.util.spec_from_file_location('gkm_master_protocol_compatibility',path)
    helper=importlib.util.module_from_spec(spec);spec.loader.exec_module(helper)
    proof=helper.check(root,active,candidate)
    if proof is not None:
        atomic(Path(root)/'out/master-protocol-compatibility.json',json_bytes(proof))
