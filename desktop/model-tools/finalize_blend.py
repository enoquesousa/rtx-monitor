"""Reset the editable source camera to its assembled presentation."""
import bpy
from pathlib import Path
from mathutils import Vector
root=Path(__file__).resolve().parents[1]
target=root/'assets/models/rtx3060-reference.blend'
bpy.ops.wm.open_mainfile(filepath=str(target))
scene=bpy.context.scene
camera=scene.camera
camera.location=(330,-330,285)
camera.rotation_euler=(Vector((30,0,10))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.ortho_scale=335
bpy.data.objects['studio_floor'].hide_render=False
scene.render.filepath=str(root/'assets/models/assembled-preview.png')
bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=str(target))
