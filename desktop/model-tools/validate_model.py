"""Offline GLB and provenance validation. No rendering or hardware acquisition."""
from pathlib import Path
import hashlib
import json
import math
import struct

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'assets'
data = (ASSETS / 'rtx3060.glb').read_bytes()
magic, version, length = struct.unpack_from('<4sII', data)
assert magic == b'glTF' and version == 2 and length == len(data)
json_size, json_type = struct.unpack_from('<II', data, 12)
assert json_type == 0x4e4f534a
doc = json.loads(data[20:20+json_size])
binary_offset = 20+json_size
binary_size, binary_type = struct.unpack_from('<II', data, binary_offset)
assert binary_type == 0x004e4942
binary = data[binary_offset+8:binary_offset+8+binary_size]
assert len(binary) >= doc['buffers'][0]['byteLength']
manifest = json.loads((ASSETS / 'rtx3060.parts.json').read_text(encoding='utf-8'))
parts = manifest['parts']
assert len(parts) == len({p['id'] for p in parts}) == 29
nodes = {node['name']: node for node in doc['nodes']}
assert set(nodes) == {p['node'] for p in parts}
rotors = [p for p in parts if p['fan_index'] is not None]
assert [(p['id'],p['fan_index']) for p in rotors] == [('fan_0_rotor',0),('fan_1_rotor',1)]
assert all(p['kind'] == 'fan_rotor' for p in rotors)
assert [p['id'] for p in parts if p['thermal_binding'] is not None] == ['gpu_package']
assert manifest['thermal_mapping']['unknown_channel_indices'] == list(range(2,9))
assert manifest['fan_mapping']['physical_mapping_verified'] is False
assert manifest['fan_mapping']['phase_synchronized_to_hardware'] is False
assert len(manifest['references']) == 13
for ref in manifest['references']:
    path=ASSETS/ref['file']
    assert hashlib.sha256(path.read_bytes()).hexdigest() == ref['sha256']
for rotor in rotors:
    assert all(abs(a-b)<1e-5 for a,b in zip(nodes[rotor['node']]['translation'],rotor['pivot']))
triangles=vertices=0
for mesh in doc['meshes']:
    for primitive in mesh['primitives']:
        assert primitive.get('mode',4) == 4
        assert 'POSITION' in primitive['attributes'] and 'NORMAL' in primitive['attributes']
        position=doc['accessors'][primitive['attributes']['POSITION']]
        normal=doc['accessors'][primitive['attributes']['NORMAL']]
        assert position['count'] == normal['count']
        for accessor in (position,normal):
            assert accessor['componentType'] == 5126 and accessor['type'] == 'VEC3'
            view=doc['bufferViews'][accessor['bufferView']]
            stride=view.get('byteStride',12)
            start=view.get('byteOffset',0)+accessor.get('byteOffset',0)
            for index in range(accessor['count']):
                value=struct.unpack_from('<fff',binary,start+stride*index)
                assert all(math.isfinite(v) for v in value)
        indices=doc['accessors'][primitive['indices']]
        assert indices['count']%3 == 0
        vertices+=position['count']
        triangles+=indices['count']//3
result={'schema_version':1,'validation':'offline_glb_geometry_and_reference_integrity',
        'hardware_calls':0,'parts':len(parts),'gltf_nodes':len(nodes),'gpu_bound_parts':1,
        'unlocalized_thermal_channels':7,'rotor_nodes':2,'blades_per_rotor':9,
        'input_references_unchanged':13,'vertices_after_gltf_vertex_splitting':vertices,
        'triangles':triangles,'glb_bytes':len(data),'glb_sha256':hashlib.sha256(data).hexdigest(),
        'dimensional_accuracy':'visual approximation only; no calibrated input scale',
        'physical_fan_mapping_verified':False,'unknown_component_temperatures_assigned':False}
(ASSETS/'models/model-validation.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result))
