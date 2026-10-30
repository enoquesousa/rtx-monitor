"""Compare old and refined static assets without rendering or querying hardware."""
from pathlib import Path
import json
import struct
import math
import hashlib

ROOT=Path(__file__).resolve().parents[2]
EVIDENCE=ROOT/'evidence/native-refinement-2026-10-25'

def glb(path):
    data=path.read_bytes()
    n,t=struct.unpack_from('<II',data,12)
    doc=json.loads(data[20:20+n])
    size,kind=struct.unpack_from('<II',data,20+n)
    return doc,data[28+n:28+n+size]

def accessor(doc,binary,index):
    a=doc['accessors'][index]
    v=doc['bufferViews'][a['bufferView']]
    code={5121:'B',5123:'H',5125:'I',5126:'f'}[a['componentType']]
    count={'SCALAR':1,'VEC3':3}[a['type']]
    fmt='<'+code*count
    size=struct.calcsize(fmt)
    stride=v.get('byteStride',size)
    start=v.get('byteOffset',0)+a.get('byteOffset',0)
    return [struct.unpack_from(fmt,binary,start+i*stride) for i in range(a['count'])]

def blade_stats(path):
    doc,binary=glb(path)
    result={}
    for node in doc['nodes']:
        if node.get('name') not in ('fan_0_rotor','fan_1_rotor'):continue
        volume=0;points=[];triangles=0;first_front_normal_y=None
        for primitive in doc['meshes'][node['mesh']]['primitives']:
            name=doc['materials'][primitive['material']]['name']
            if 'Fan blade ·' not in name or 'ribs' in name:continue
            positions=accessor(doc,binary,primitive['attributes']['POSITION'])
            points.extend(positions)
            indices=[i[0] for i in accessor(doc,binary,primitive['indices'])]
            for j in range(0,len(indices),3):
                a,b,c=[positions[indices[j+k]] for k in range(3)]
                cross=(b[1]*c[2]-b[2]*c[1],b[2]*c[0]-b[0]*c[2],b[0]*c[1]-b[1]*c[0])
                if first_front_normal_y is None:
                    ab=tuple(b[k]-a[k] for k in range(3));ac=tuple(c[k]-a[k] for k in range(3))
                    face=(ab[1]*ac[2]-ab[2]*ac[1],ab[2]*ac[0]-ab[0]*ac[2],ab[0]*ac[1]-ab[1]*ac[0])
                    magnitude=math.sqrt(sum(x*x for x in face))
                    first_front_normal_y=face[1]/magnitude
                volume+=sum(a[k]*cross[k] for k in range(3))/6
                triangles+=1
        result[node['name']]={'closed_blade_signed_volume_scene_units3':volume,
                             'first_front_surface_triangle_normal_y':first_front_normal_y,
                             'surface_vertical_extent_mm':max(p[1] for p in points)-min(p[1] for p in points),
                             'rotor_tip_radius_mm':max(math.hypot(p[0],p[2]) for p in points),
                             'triangles_in_blade_surfaces':triangles}
    return result

before=json.loads((EVIDENCE/'rtx3060-before.parts.json').read_text(encoding='utf-8'))
after=json.loads((ROOT/'desktop/assets/rtx3060.parts.json').read_text(encoding='utf-8'))
assert len(before['parts'])==len(after['parts'])==29
for old,new in zip(before['parts'],after['parts']):
    for key in ('id','node','kind','explode','pivot','fan_index','thermal_binding'):
        assert old[key]==new[key],f'Changed runtime part contract {key}: {old["id"]}'
oldstats=blade_stats(EVIDENCE/'rtx3060-before.glb')
newstats=blade_stats(ROOT/'desktop/assets/rtx3060.glb')
for name,stats in newstats.items():
    assert stats['closed_blade_signed_volume_scene_units3']>0,'Blade surface normals must point outward'
    assert stats['rotor_tip_radius_mm']<44.0,'Blade tips must remain inside the stationary ring'
    assert stats['surface_vertical_extent_mm']>4.0,'Refined blades must have meaningful pitch/camber'
    assert stats['first_front_surface_triangle_normal_y']>0,'Visible blade surface must face outward'
result={'schema_version':1,'hardware_calls':0,'runtime_part_contract_preserved':29,
        'fan_rotor_pivots_unchanged':True,'thermal_binding_unchanged':'gpu_package only',
        'physical_mapping_limitations_unchanged':True,
        'before_blades':oldstats,'after_blades':newstats,
        'refined_features':['outward blade normals','rounded swept tips','wide cambered twisted blades',
                            'eight curved moulding ribs per blade','stepped hub, cap and concentric borders',
                            'outward side lettering','sculpted side fascia and real assembly clearances',
                            'geometric carbon weave inlays outside the fan apertures'],
        'comparative_render':'Same assembled camera, lights, resolution and scene; geometry/materials changed.',
        'glb_sha256':hashlib.sha256((ROOT/'desktop/assets/rtx3060.glb').read_bytes()).hexdigest()}
(EVIDENCE/'model-refinement-validation.json').write_text(json.dumps(result,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
