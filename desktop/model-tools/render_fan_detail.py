"""Static CPU macro render of the refined fan, with no geometry changes."""
import bpy
from pathlib import Path
from mathutils import Vector
root=Path(__file__).resolve().parents[2]
bpy.ops.wm.open_mainfile(filepath=str(root/'desktop/assets/models/rtx3060-reference.blend'))
scene=bpy.context.scene
camera=scene.camera
camera.location=(58,-66,139)
camera.rotation_euler=(Vector((-30,0,30))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.ortho_scale=130
scene.render.resolution_x=1200
scene.render.resolution_y=1000
scene.render.resolution_percentage=100
scene.render.filepath=str(root/'evidence/native-refinement-2026-10-25/fan-detail-after.png')
bpy.ops.render.render(write_still=True)
