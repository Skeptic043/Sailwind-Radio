"""Read-only reopened .blend and exported mesh verification. No original project writes."""
import bpy,json,math,hashlib
from pathlib import Path
root=Path(__file__).resolve().parents[2]
asset=root/'assets/runtime/devices.json'
data=json.loads(asset.read_text())
assert data['version']==1 and len(data['models'])==4
results=[]
for model in data['models']:
    kind=model['kind'];assert kind in range(4)
    objects=[o for o in bpy.data.objects if o.get('kind')==kind and not o.get('preview')]
    assert objects and all(o.type=='MESH' for o in objects)
    names={p['name'] for p in model['parts']}
    assert 'control_power' in names and 'icon_power' in names
    assert ('control_bass' in names)==(kind==3)
    assert ('control_master' in names)==(kind==0)
    assert ('control_volume' in names)==(kind in (1,2))
    triangles=sum(len(p['triangles'])//3 for p in model['parts'])
    assert triangles<=[3000,1000,1800,2000][kind]
    for p in model['parts']:
        assert len(p['vertices'])==len(p['normals']) and len(p['vertices'])%3==0
        assert len(p['triangles'])%3==0
        assert all(math.isfinite(v) for v in p['vertices']+p['normals'])
        assert len(p['pivot'])==3 and all(math.isfinite(v) for v in p['pivot'])
        assert all(0<=i<len(p['vertices'])//3 for i in p['triangles'])
    results.append({'kind':kind,'authoring_objects':len(objects),'parts':len(model['parts']),'triangles':triangles})
report={'status':'passed','blender':bpy.app.version_string,'runtime_sha256':hashlib.sha256(asset.read_bytes()).hexdigest(),'models':results}
(root/'assets/authoring/reopen-verification.json').write_text(json.dumps(report,indent=2))
print('RADIO_MODEL_REOPEN_PASSED')
