"""Render the existing assembly without modifying the GLB or source .blend."""
import bpy
import json
from pathlib import Path
from mathutils import Vector
root=Path(__file__).resolve().parents[1]
manifest=json.loads((root/'assets/rtx3060.parts.json').read_text(encoding='utf-8'))
bpy.ops.wm.open_mainfile(filepath=str(root/'assets/models/rtx3060-reference.blend'))
scene=bpy.context.scene
for part in manifest['parts']:
    obj=bpy.data.objects.get(part['node'])
    e=part['explode']
    obj.location+=Vector((e[0],-e[2],e[1]))
bpy.data.objects['studio_floor'].hide_render=True
camera=scene.camera
camera.location=(375,-420,340)
camera.rotation_euler=(Vector((25,0,65))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.ortho_scale=620
scene.render.filepath=str(root/'assets/models/exploded-preview.png')
bpy.ops.render.render(write_still=True)
