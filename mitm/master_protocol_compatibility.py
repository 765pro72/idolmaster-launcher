from pathlib import Path
import struct
import hashlib
import re

EXPECTED = {
 'MasterGetResponse': {'MasterTagFieldNumber':1,'ProtoHashFieldNumber':2},
 'MasterTag': {'VersionFieldNumber':1,'MasterTagPacksFieldNumber':2},
 'MasterTagPack': {'TypeFieldNumber':1,'FileNameFieldNumber':2,'FileSizeFieldNumber':3,'CryptoKeyFieldNumber':4,'DownloadUrlFieldNumber':5},
}

class ProtocolCompatibilityError(ValueError):
    permanent_compatibility_failure = True


def master_envelope_schema(data):
    if len(data)<256 or struct.unpack_from('<II',data,0)!=(0xFAB11BAF,31):
        raise ProtocolCompatibilityError('Unsupported client metadata format; compatibility review required.')
    def section(at, stride=1):
        offset,size=struct.unpack_from('<ii',data,at)
        if offset<256 or size<0 or offset+size>len(data) or size%stride:
            raise ProtocolCompatibilityError('Invalid client metadata section.')
        return offset,size
    so,ss=section(24);to,ts=section(160,88);fo,fs=section(96,12)
    do,ds=section(64,12);vo,vs=section(72)
    def string(index):
        if not 0<=index<ss:raise ProtocolCompatibilityError('Invalid metadata string index.')
        end=data.find(b'\0',so+index,so+ss)
        if end<0:raise ProtocolCompatibilityError('Unterminated metadata string.')
        return data[so+index:end].decode('utf-8')
    def constant(index):
        if not 0<=index<vs:raise ProtocolCompatibilityError('Invalid metadata constant index.')
        p=vo+index;x=data[p]
        if x<128:n=x;length=1
        elif x<192:n=((x&63)<<8)|data[p+1];length=2
        elif x<224:n=((x&31)<<24)|(data[p+1]<<16)|(data[p+2]<<8)|data[p+3];length=4
        elif x==240:n=struct.unpack_from('<I',data,p+1)[0];length=5
        else:raise ProtocolCompatibilityError('Unsupported metadata integer encoding.')
        if index+length>vs:raise ProtocolCompatibilityError('Truncated metadata constant.')
        return ~(n>>1) if n&1 else n>>1
    defaults={struct.unpack_from('<iii',data,i)[0]:struct.unpack_from('<iii',data,i)[2]
              for i in range(do,do+ds,12)}
    result={}
    for p in range(to,to+ts,88):
        name=string(struct.unpack_from('<i',data,p)[0])
        if name not in EXPECTED:continue
        if name in result:raise ProtocolCompatibilityError('Ambiguous master envelope type.')
        start=struct.unpack_from('<i',data,p+32)[0];count=struct.unpack_from('<H',data,p+68)[0]
        if start<0 or (start+count)*12>fs:raise ProtocolCompatibilityError('Invalid metadata field range.')
        fields={}
        for f in range(start,start+count):
            field=string(struct.unpack_from('<i',data,fo+f*12)[0])
            if field.endswith('FieldNumber'):
                if f not in defaults:raise ProtocolCompatibilityError('Missing master field number.')
                fields[field]=constant(defaults[f])
        result[name]=fields
    if result!=EXPECTED:
        raise ProtocolCompatibilityError('Current client master envelope differs from the supported schema; existing content retained.')
    return result


def check(root, active, candidate):
    if active['proto_hash']==candidate['proto_hash']:return None
    root=Path(root)
    if not re.fullmatch(rb'[0-9a-fA-F]{40}',candidate['proto_hash']):
        raise ProtocolCompatibilityError('Unrecognized protocol hash format; compatibility review required.')
    try:
        game=Path((root/'game_path.txt').read_text('utf-8-sig').strip().strip('"'))
        metadata=game/'gakumas_Data/il2cpp_data/Metadata/global-metadata.dat'
        data=metadata.read_bytes()
        schema=master_envelope_schema(data)
    except ProtocolCompatibilityError:raise
    except (OSError,ValueError,IndexError,struct.error,UnicodeError) as exc:
        raise ProtocolCompatibilityError('Cannot verify the selected client master envelope: '+type(exc).__name__) from exc
    return {'scope':'master-manifest-envelope-only','metadata_version':31,
            'metadata_sha256':hashlib.sha256(data).hexdigest(),
            'previous_proto_hash':active['proto_hash'].decode('ascii'),
            'candidate_proto_hash':candidate['proto_hash'].decode('ascii'),
            'candidate_master_version':candidate['version'],'schema':schema,
            'cache_integrity_still_required':True}
