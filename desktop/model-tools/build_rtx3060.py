"""Reference-guided GPU assembly, generated in an isolated Blender process.

No photograph is edited. All dimensions are visual approximations, not caliper
measurements, PCB manufacturing data, a thermal simulation or an electrical netlist.
Run Blender --background --factory-startup --python this_file.py.
"""
from pathlib import Path
import bpy
from mathutils import Vector
import math
import json
import hashlib
import random
import bmesh

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'assets'
MODELS = ASSETS / 'models'
MODELS.mkdir(parents=True, exist_ok=True)
REFS = ASSETS / 'references'
REFS.mkdir(exist_ok=True)
RNG = random.Random(306012)

bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
for block in list(bpy.data.materials):
    bpy.data.materials.remove(block)
scene = bpy.context.scene
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = .001
parts = []
model_objects = []

def material(name, color, metal=0.0, rough=.5):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    p = m.node_tree.nodes.get('Principled BSDF')
    p.inputs['Base Color'].default_value = (*color, 1)
    p.inputs['Metallic'].default_value = metal
    p.inputs['Roughness'].default_value = rough
    m.diffuse_color = (*color, 1)
    return m

M = {
    'pcb': material('PCB · charcoal solder mask', (.025,.031,.029), .12,.7),
    'pcb_edge': material('FR4 · board edge', (.15,.13,.075), .1,.7),
    'silicon': material('Silicon · polished die', (.47,.49,.51), .75,.22),
    'package': material('GPU substrate · dark teal', (.015,.125,.135), .22,.52),
    'memory': material('Memory packages · graphite', (.045,.049,.05), .08,.55),
    'black': material('Shroud · textured black', (.021,.025,.027), .08,.65),
    'accent': material('Shroud · satin charcoal', (.065,.073,.077), .28,.4),
    'carbon': material('Shroud · carbon weave accents', (.10,.105,.11), .45,.4),
    'silver': material('Aluminium · fin edges', (.57,.60,.62), .88,.3),
    'steel': material('Steel · bracket', (.39,.41,.43), .9,.3),
    'copper': material('Copper · heatpipes', (.48,.22,.085), .9,.25),
    'gold': material('Gold · PCIe contact fingers', (.58,.40,.10), .85,.24),
    'capacitor': material('Capacitor · brushed aluminium', (.43,.47,.49), .75,.4),
    'cap_blue': material('Capacitor · blue top markings', (.12,.38,.50), .35,.45),
    'label': material('Printed lettering · off white', (.73,.76,.77), .15,.5),
    'solder': material('Solder · contact pads', (.29,.32,.32), .7,.4),
    'pad': material('Thermal pad · grey blue', (.30,.38,.42), .0,.9),
    'fan': material('Fan blade · moulded charcoal', (.028,.033,.038), .08,.30),
    'fan_rib': material('Fan blade · curved moulding ribs', (.045,.051,.057), .10,.36),
    'fan_cap': material('Fan hub · satin polymer cap', (.043,.049,.055), .10,.45),
    'carbon_dark': material('Carbon weave · dark tow', (.029,.034,.039), .35,.47),
    'carbon_light': material('Carbon weave · light tow', (.078,.086,.094), .40,.39),
    'coil': material('VRM inductor · ferrite', (.10,.115,.12), .35,.5),
}

def assign(obj, key):
    obj.data.materials.append(M[key])
    return obj

def bevel(obj, amount=.4, segments=2):
    mod = obj.modifiers.new('Manufactured edge radius', 'BEVEL')
    mod.width = amount
    mod.segments = segments
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=mod.name)
    return obj

def box(name, loc, dims, mat='black', radius=0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    obj = bpy.context.object
    obj.name = name
    obj.dimensions = dims
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    assign(obj, mat)
    if radius:
        bevel(obj, radius)
    return obj

def cylinder(name, loc, radius, depth, mat='steel', vertices=32):
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth, location=loc)
    obj = bpy.context.object
    obj.name = name
    assign(obj, mat)
    for poly in obj.data.polygons:
        if len(poly.vertices)==4:
            poly.use_smooth=True
    return obj

def ring(name, loc, outer, inner, height, mat='steel', count=32):
    verts=[]
    for z in (-height/2,height/2):
        for r in (outer,inner):
            verts.extend([(loc[0]+r*math.cos(i*2*math.pi/count),loc[1]+r*math.sin(i*2*math.pi/count),loc[2]+z) for i in range(count)])
    faces=[]
    for i in range(count):
        j=(i+1)%count
        faces.extend([(i,j,2*count+j,2*count+i),
                      (count+j,count+i,3*count+i,3*count+j),
                      (2*count+i,2*count+j,3*count+j,3*count+i),
                      (j,i,count+i,count+j)])
    mesh=bpy.data.meshes.new(name)
    mesh.from_pydata(verts,[],faces)
    mesh.update()
    obj=bpy.data.objects.new(name,mesh)
    scene.collection.objects.link(obj)
    assign(obj,mat)
    return obj

def mesh(name, verts, faces, mat):
    m=bpy.data.meshes.new(name)
    m.from_pydata(verts,[],faces)
    m.update()
    o=bpy.data.objects.new(name,m)
    scene.collection.objects.link(o)
    assign(o,mat)
    return o

def extrude_polygon(name, polygon, z, thickness, mat):
    signed=sum(polygon[i][0]*polygon[(i+1)%len(polygon)][1]-polygon[(i+1)%len(polygon)][0]*polygon[i][1] for i in range(len(polygon)))
    if signed<0: polygon=list(reversed(polygon))
    n=len(polygon)
    verts=[(x,y,z+dz) for dz in (-thickness/2,thickness/2) for x,y in polygon]
    faces=[tuple(reversed(range(n))),tuple(range(n,2*n))]
    faces.extend([(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)])
    return mesh(name,verts,faces,mat)

def extrude_xz_polygon(name, polygon, y, thickness, mat):
    """A vertical side fascia with outward normals and real edge thickness."""
    obj=extrude_polygon(name,polygon,0,thickness,mat)
    for vertex in obj.data.vertices:
        x,z,d=vertex.co
        vertex.co=(x,y+d,z)
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(obj.data);bm.free()
    return obj

def outward_normals(obj):
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(obj.data);bm.free()
    obj.data.update()
    return obj

def carbon_weave(name, polygon, z):
    """Small raised tow rectangles: geometry, not a painted image texture."""
    def inside(x,y):
        value=False
        for i,p in enumerate(polygon):
            q=polygon[(i+1)%len(polygon)]
            if (p[1]>y)!=(q[1]>y) and x<(q[0]-p[0])*(y-p[1])/(q[1]-p[1])+p[0]: value=not value
        return value
    vertices=[];faces=[];indices=[]
    minx=min(p[0] for p in polygon);maxx=max(p[0] for p in polygon)
    miny=min(p[1] for p in polygon);maxy=max(p[1] for p in polygon)
    xi=0;x=minx
    while x<maxx:
        yi=0;y=miny
        while y<maxy:
            theta=math.pi/4 if (xi+yi)%2 else -math.pi/4
            corners=[]
            for xx,yy in ((-.73,-.30),(.73,-.30),(.73,.30),(-.73,.30)):
                px=x+xx*math.cos(theta)-yy*math.sin(theta)
                py=y+xx*math.sin(theta)+yy*math.cos(theta)
                corners.append((px,py,z))
            outside_fan=all((px-cx)**2+py**2>47.85**2 for px,py,_ in corners for cx in (-30,96))
            if all(inside(px,py) for px,py,_ in corners) and outside_fan:
                n=len(vertices);vertices.extend(corners);faces.append((n,n+1,n+2,n+3));indices.append((xi+yi)%2)
            yi+=1;y+=1.15
        xi+=1;x+=1.25
    obj=mesh(name,vertices,faces,'carbon_dark')
    obj.data.materials.append(M['carbon_light'])
    for face,index in zip(obj.data.polygons,indices):face.material_index=index
    return obj

def difference(obj, cutter):
    bpy.context.view_layer.objects.active=obj
    mod=obj.modifiers.new('Geometric opening', 'BOOLEAN')
    mod.operation='DIFFERENCE'
    mod.solver='EXACT'
    mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(cutter,do_unlink=True)

def join(name, objects, label, kind, explode=(0,0,0), pivot=(0,0,0), fan_index=None, thermal_binding=None):
    bpy.ops.object.select_all(action='DESELECT')
    for obj in objects: obj.select_set(True)
    bpy.context.view_layer.objects.active=objects[0]
    bpy.ops.object.join()
    obj=bpy.context.object
    obj.name=name
    scene.cursor.location=pivot
    bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
    # Standard glTF Y-up: Blender X,Y,Z becomes glTF X,Z,-Y.
    gp=[pivot[0],pivot[2],-pivot[1]]
    ge=[explode[0],explode[2],-explode[1]]
    record={'id':name,'node':name,'name':name,'label':label,'kind':kind,
            'explode':ge,'pivot':gp,'fan_index':fan_index,'thermal_binding':thermal_binding}
    parts.append(record)
    obj['rtx_part_id']=name
    obj['measurement_binding']=thermal_binding or 'unmeasured'
    model_objects.append(obj)
    return obj

def text_object(name, text, loc, size, mat='label', rotation=(0,0,0), extrude=.015):
    bpy.ops.object.text_add(location=loc, rotation=rotation)
    obj=bpy.context.object
    obj.name=name
    obj.data.body=text
    obj.data.align_x='CENTER'
    obj.data.align_y='CENTER'
    obj.data.size=size
    obj.data.extrude=extrude
    obj.data.resolution_u=2
    obj.data.materials.append(M[mat])
    bpy.ops.object.convert(target='MESH')
    return bpy.context.object

def pipe(name, coords, radius=2.8, mat='copper'):
    data=bpy.data.curves.new(name,'CURVE')
    data.dimensions='3D'
    data.resolution_u=6
    data.bevel_depth=radius
    data.bevel_resolution=3
    spl=data.splines.new('BEZIER')
    spl.bezier_points.add(len(coords)-1)
    for p,co in zip(spl.bezier_points,coords):
        p.co=co
        p.handle_left_type='AUTO'
        p.handle_right_type='AUTO'
    obj=bpy.data.objects.new(name,data)
    scene.collection.objects.link(obj)
    assign(obj,mat)
    bpy.context.view_layer.objects.active=obj
    obj.select_set(True)
    bpy.ops.object.convert(target='MESH')
    return bpy.context.object

# Short PCB and the long cooler extension follow the component photographs.
outline=[(-89,-53),(-82,-56),(76,-56),(88,-48),(88,46),(78,53),(-82,53),(-89,47)]
pcb=extrude_polygon('pcb_raw',outline,0,1.8,'pcb')
mounts=[(-82,-44),(-82,44),(-62,-42),(-62,42),(-1,-26),(45,-26),(-1,26),(45,26),(79,-44),(79,43)]
for i,(x,y) in enumerate(mounts):
    cutter=cylinder('pcb_hole',(x,y,0),2.2,6,vertices=24)
    difference(pcb,cutter)
bevel(pcb,.35,2)
join('pcb', [pcb], 'PCB · face e verso', 'pcb')

rings=[ring('mount_ring',(x,y,1.0),3.4,2.2,.2,'copper',24) for x,y in mounts]
join('pcb_mounting_rings',rings,'Anéis de montagem e cobre exposto','hardware')

edge=[box('edge_substrate',(-6,-57,-.1),(114,7.2,1.45),'pcb_edge',.2)]
for side in (-1,1):
    for i in range(11):
        edge.append(box('pcie_contact',(-60+i*.96,-57.0,side*.87),(.68,5.9,.12),'gold'))
    for i in range(71):
        edge.append(box('pcie_contact',(-44+i*.96,-57.0,side*.87),(.68,5.9,.12),'gold'))
join('pcie_fingers',edge,'Conector PCIe x16 · contatos visuais','connector')

# Physical chip regions are modeled; only the GPU component has a proven thermal target.
gpu=[box('gpu_substrate',(22,0,1.75),(43,43,1.65),'package',.6),
     box('gpu_underfill',(22,0,2.66),(23,19,.35),'black',.2),
     box('gpu_die_surface',(22,0,3.0),(20.5,16,.5),'silicon',.4)]
for i in range(12):
    gpu.append(box('gpu_passive',(3.0+i*3.45,17.8,2.7),(1.3,.7,.35),'solder',.1))
join('gpu_package',gpu,'GPU · encapsulamento e die','gpu',explode=(0,0,16),thermal_binding='gpu_package')

memory_positions=[(-8,35),(17,35),(42,35),(60,-27),(60,-2),(60,23)]
for i,(x,y) in enumerate(memory_positions):
    objects=[box('dram_package',(x,y,2.1),(18.5,19.5,2.2),'memory',.45),
             text_object('dram_mark',f'GDDR6  {i+1}',(x,y,3.23),1.5,'label')]
    join(f'vram_{i}',objects,f'VRAM · pacote {i+1} (sem sensor localizado)','memory',explode=(0,0,13))

coils=[]
for i,y in enumerate((-37,-18,1,20,39)):
    coils.append(box('inductor',(-57,y,6.0),(10,13,9),'coil',.9))
    coils.append(text_object('coil_mark','R22',(-57,y,10.6),2.1,'accent'))
join('vrm_inductors',coils,'VRM · indutores (sem medição localizada)','vrm',explode=(0,0,11))

caps=[]
for x in (-72,-39):
    for i,y in enumerate((-43,-24,-5,14,33,46)):
        caps.append(cylinder('capacitor',(x,y,5.4),3.9,8.7,'capacitor',24))
        caps.append(cylinder('capacitor_top',(x,y,9.8),3.4,.18,'capacitor',24))
        caps.append(box('cap_marker',(x,y,9.93),(3.2,.55,.08),'cap_blue'))
        caps.append(box('cap_marker',(x,y,9.93),(.55,3.2,.08),'cap_blue'))
for y in (-42,39):
    caps.append(cylinder('capacitor',(80,y,5.2),4.0,8,'capacitor',24))
join('vrm_capacitors',caps,'VRM · capacitores (sem medição localizada)','vrm',explode=(0,0,11))

mosfets=[]
for y in (-37,-18,1,20,39):
    mosfets.append(box('mosfet',(-57,y-7.5,2.7),(8,3.5,2.8),'memory',.2))
    for xx in (-61,-53):
        for yy in (-1,0,1):
            mosfets.append(box('mosfet_contact',(xx,y-7.5+yy,1.55),(.8,.5,.25),'solder'))
join('vrm_power_stages',mosfets,'VRM · estágios de potência','vrm',explode=(0,0,10))

power=[box('8pin_body',(77,43,8.2),(18,16,14.6),'black',.7)]
for xx in (-1,1):
    for yy in range(4):
        power.append(box('8pin_socket',(77+xx*4,36.9+yy*4,15.55),(3,3,.4),'accent',.3))
power.append(box('8pin_clip',(77,51.4,10.5),(7,2.3,6),'black',.3))
join('power_8pin',power,'Conector auxiliar · 8 pinos','connector')

# Representative visible SMD detail, explicitly not a reconstructed electrical netlist.
tiny=[]
for i in range(310):
    for attempt in range(100):
        x=RNG.uniform(-84,84); y=RNG.uniform(-48,48)
        occupied=((-4<x<48 and -26<y<26) or (-20<x<54 and 23<y<48) or (48<x<72 and -40<y<36)
                  or (-78<x<-33) or (65<x<87 and y>30))
        if not occupied: break
    else: continue
    w=RNG.choice((.65,.9,1.2)); d=RNG.choice((.5,.6,.8))
    tiny.append(box('smd_body',(x,y,1.2),(w,d,.55),'memory'))
    tiny.append(box('smd_pad',(x-w/2-.1,y,1.12),(.2,d,.2),'solder'))
    tiny.append(box('smd_pad',(x+w/2+.1,y,1.12),(.2,d,.2),'solder'))
for i in range(180):
    x=RNG.uniform(-84,83); y=RNG.uniform(-47,47)
    if -3<x<48 and -26<y<26: continue
    tiny.append(box('rear_smd',(x,y,-1.15),(RNG.uniform(.6,1.6),.6,.4),'memory'))
join('pcb_smd_details',tiny,'SMDs · detalhe visual aproximado, não netlist','pcb_detail')

ports=[]
for i,y in enumerate((-40,-14,12,38)):
    ports.append(box('io_housing',(-83,y,4.4),(16,19,8.9),'steel',.65))
    ports.append(box('io_recess',(-91.3,y,4.3),(.25,15.8,6.4),'black',.5))
    ports.append(box('io_tongue',(-91.52,y,3.8),(.28,11.3,.7),'accent',.12))
    for j in range(10):
        ports.append(box('io_pin',(-91.7,y-5+j*1.1,4.18),(.18,.42,.18),'gold'))
join('display_ports',ports,'Saídas de vídeo · três DP e uma HDMI, representação','connector')

# I/O bracket built as genuine open bars, not a painted perforation texture.
bracket=[]
for z in (-6,12.2,35.5):
    bracket.append(box('bracket_rail',(-93,0,z),(1.25,119,1.4),'steel',.2))
for y in (-59,59):
    bracket.append(box('bracket_edge',(-93,y,14),(1.25,1.6,44),'steel',.2))
for y in (-53,-27,-1,25,51):
    bracket.append(box('port_separator',(-93,y,3),(1.25,2.4,18),'steel',.15))
for i in range(23):
    bracket.append(box('vent_column',(-93,-56+i*4.95,24),(1.2,.9,21),'steel',.12))
for z in (15,20.1,25.2,30.3,35.1):
    bracket.append(box('vent_row',(-93,0,z),(1.2,115,.9),'steel',.12))
bracket.append(box('bracket_flange',(-88,59.7,14),(11,1.1,44),'steel',.25))
bracket.append(box('mount_tab',(-92,65,31),(1.25,13,9),'steel',.3))
join('io_bracket',bracket,'Espelho traseiro · aberturas e fixação','bracket',explode=(-32,0,0))

base=[box('gpu_contact_base',(22,0,5.3),(67,75,3.1),'silver',1.0),
      box('die_contact_insert',(22,0,3.7),(23,19,.8),'copper',.5)]
join('heatsink_base',base,'Dissipador · base de contato (temperatura não medida)','heatsink',explode=(0,0,46))
pads=[]
for x,y in memory_positions:
    pads.append(box('thermal_pad',(x,y,3.75),(17.2,18.1,.85),'pad',.3))
join('thermal_pads',pads,'Thermal pads · geometria ilustrativa, sem medição','thermal_interface',explode=(0,0,30))

fins=[]
for start,end in ((-86,-12),(58,151)):
    count=int((end-start)/1.8)
    for i in range(count+1):
        x=start+i*(end-start)/count
        fins.append(box('aluminium_fin',(x,0,16),(.33,101,18.0),'silver',.1))
for y in (-51,51):
    fins.append(box('fin_edge_band',(32,y,22.5),(238,1,2.2),'silver',.2))
join('heatsink_fins',fins,'Dissipador · dois bancos de aletas','heatsink',explode=(0,0,46))

for i,spread in enumerate((31,18)):
    coords=[(-83,-spread,15),(-17,-spread,15),(2,-spread*.67,6.4),(42,-spread*.67,6.4),
            (64,-spread,14),(144-i*9,-spread,14),(153-i*9,0,14),(144-i*9,spread,14),
            (64,spread,14),(42,spread*.67,6.4),(2,spread*.67,6.4),(-17,spread,15),(-83,spread,15)]
    join(f'heatpipe_{i}',[pipe('bent_heatpipe',coords,2.75)],f'Heatpipe {i+1} · sem sensor localizado','heatpipe',explode=(0,0,46))

# Curved injection-moulded 9-blade rotors. The geometry has an airfoil-like
# cambered surface, rounded swept tips and real raised moulding ribs.
# This is visual reconstruction, not aerodynamic engineering or a measured profile.
# Fan identity to physical left/right remains a visual convention.
fan_centers=(-30,96)
for fi,cx in enumerate(fan_centers):
    z0=29.5
    phase=0.0 if fi==0 else -.17
    rotor=[bevel(cylinder('rotor_hub',(cx,0,z0-.15),14.05,5.7,'black',64),.40,3),
           bevel(cylinder('rotor_upper_shoulder',(cx,0,z0+2.55),13.55,1.25,'fan_cap',64),.25,3),
           cylinder('rotor_label',(cx,0,z0+3.25),12.60,.35,'fan_cap',64),
           ring('hub_outer_trim',(cx,0,z0+3.37),13.11,12.88,.24,'accent',64),
           ring('hub_label_border',(cx,0,z0+3.46),12.65,12.49,.14,'label',64)]
    logo=text_object('fan_logo','GALAX',(cx,0,z0+3.53),4.45,'label')
    logo.rotation_euler.z=phase
    rotor.append(logo)
    for blade in range(9):
        angle=blade*2*math.pi/9+phase
        def blade_point(t,u,lower=0.0):
            r=10.85+(43.8-10.85)*t-2.4*abs(2*u-1)**4*t**6
            sweep=-.62*t+.17*t*t
            width=.36+.20*math.sin(math.pi*t*.9)+.12*t
            a=angle+sweep+(u-.5)*width
            camber=.60*math.sin(math.pi*u)*math.sin(math.pi*t)
            twist=4.7*(u-.5)*(.30+.70*t)
            zz=z0-1.70+2.35*math.sin(math.pi*t*.9)+twist+camber-lower
            return (cx+r*math.cos(a),r*math.sin(a),zz)
        verts=[]
        radial=16; across=9
        for lower in (0,1):
            for ri in range(radial+1):
                for ai in range(across+1):
                    verts.append(blade_point(ri/radial,ai/across,lower*.62))
        faces=[]; layer=(radial+1)*(across+1)
        for ri in range(radial):
            for ai in range(across):
                n=ri*(across+1)+ai
                # The visible front points outward (+Blender Z / +glTF Y).
                # The original draft reversed this winding and looked flat/dark
                # under the native renderer's normal-based lighting.
                faces.extend([(n,n+across+1,n+across+2,n+1),
                              (layer+n,layer+n+1,layer+n+across+2,layer+n+across+1)])
        boundary=list(range(across+1))+[r*(across+1)+across for r in range(1,radial+1)]+[radial*(across+1)+a for a in range(across-1,-1,-1)]+[r*(across+1) for r in range(radial-1,0,-1)]
        for a,b in zip(boundary,boundary[1:]+boundary[:1]):faces.append((a,b,layer+b,layer+a))
        b=outward_normals(mesh('curved_fan_blade',verts,faces,'fan'))
        for p in b.data.polygons:p.use_smooth=True
        rotor.append(b)
        # Follow the actual cambered surface; ribs are not straight painted rays.
        ribverts=[];ribfaces=[]
        for t in (.20,.31,.42,.53,.64,.75,.86,.94):
            start=len(ribverts)
            for edge in (-1,1):
                for step in range(13):
                    u=.055+.89*step/12
                    point=blade_point(t+edge*.0032,u)
                    ribverts.append((point[0],point[1],point[2]+.065))
            for step in range(12):
                ribfaces.append((start+step,start+13+step,start+14+step,start+1+step))
        ribs=mesh('blade_moulding_ribs',ribverts,ribfaces,'fan_rib')
        for p in ribs.data.polygons:p.use_smooth=True
        rotor.append(ribs)
    join(f'fan_{fi}_rotor',rotor,f'Rotor {fi} · nove pás, índice lógico convencional','fan_rotor',explode=(0,0,100),pivot=(cx,0,z0),fan_index=fi)
    frame=[ring('fan_outer_rim',(cx,0,27.3),46.2,45.0,2.0,'black',96),
           ring('fan_rim_lip',(cx,0,28.35),46.1,45.2,.5,'accent',96),
           cylinder('stator_motor',(cx,0,25.4),10.5,3.0,'black',32)]
    for a in (math.pi/6,5*math.pi/6,3*math.pi/2):
        xx=cx+28*math.cos(a); yy=28*math.sin(a)
        arm=box('stator_arm',(xx,yy,24.9),(39,3.1,1.6),'black',.7)
        arm.rotation_euler.z=a
        frame.append(arm)
        mx=cx+45*math.cos(a); my=45*math.sin(a)
        frame.append(ring('fan_mount',(mx,my,26),4.2,1.7,2,'black',24))
    wirecoords=[(cx-8,-6,24.5),(cx-19,-16,23),(cx-32,-37,22),(cx-25,-49,19),(cx-10,-49,10)]
    frame.append(pipe('fan_lead',wirecoords,.9,'black'))
    frame.append(box('fan_connector',(cx-10,-49,9),(5,4,3),'label',.4))
    join(f'fan_{fi}_stator',frame,f'Estrutura e cabo do cooler {fi}','fan_frame',explode=(0,0,82))

shroud_poly=[(-92,-48),(-84,-56),(148,-56),(158,-46),(158,46),(148,56),(-84,56),(-92,48)]
shroud=extrude_polygon('shroud_shell',shroud_poly,29.5,6.2,'black')
for cx in fan_centers:
    cutter=cylinder('fan_aperture',(cx,0,29.5),47.3,20,vertices=96)
    difference(shroud,cutter)
bevel(shroud,.8,3)
shroud_objects=[shroud]
for sign in (-1,1):
    for left,right,yoffset in ((-89,-58,48),(-1,31,48),(76,111,48),(133,153,43)):
        poly=[(left,sign*(yoffset-4)),(right,sign*(yoffset+1)),(right-4,sign*(yoffset+7)),(left+4,sign*(yoffset+4))]
        accent=extrude_polygon('carbon_accent',poly,32.9,.5,'carbon_dark')
        for cx in fan_centers:
            difference(accent,cylinder('inlay_aperture',(cx,0,33),47.7,4,vertices=96))
        shroud_objects.append(accent)
        shroud_objects.append(carbon_weave('geometric_carbon_weave',poly,33.18))
    shroud_objects.append(box('outer_edge_bead',(33,sign*54.4,30.8),(231,1.2,1.7),'accent',.4))
    fascia=[(-88,18),(-88,25),(-79,31.6),(143,31.6),(155,26),(155,18.5),(139,16.9),
            (104,19.2),(69,17.5),(34,21.6),(-2,17.6),(-52,19.5)]
    if sign<0:
        fascia=[(x,22.0+(z-18)*.72) for x,z in fascia]
    side=extrude_xz_polygon('sculpted_side_profile',fascia,sign*55.5,2.3,'black')
    bevel(side,.45,2)
    shroud_objects.append(side)
    shroud_objects.append(box('side_upper_seam',(32,sign*56.7,30.7),(230,.45,.65),'accent',.2))
shroud_objects.append(extrude_polygon('center_badge',[(27,-10),(37,-10),(41,0),(37,10),(27,10),(23,0)],33.05,.65,'accent'))
shroud_objects.append(text_object('center_badge_text','RTX',(32,0,33.45),3.8,'black'))
# Side lettering is mesh geometry so it survives native renderers without textures.
shroud_objects.append(text_object('side_logo','GALAX',(-40,56.76,24.9),7.5,'label',rotation=(-math.pi/2,0,0)))
shroud_objects.append(text_object('side_rtx','GEFORCE RTX',(101,56.76,24.9),6.0,'label',rotation=(-math.pi/2,0,0)))
join('front_shroud',shroud_objects,'Carenagem frontal · dois recortes circulares','shroud',explode=(0,0,136))

# Backplate: substantial plate plus open geometric honeycomb on cooler extension.
back_left=box('backplate_solid',(-21,0,-4.1),(143,109,2.1),'black',2.2)
for x,y in ((-1,-26),(45,-26),(-1,26),(45,26)):
    difference(back_left,cylinder('backplate_hole',(x,y,-4),3.5,8,vertices=32))
back=[back_left,box('backplate_upper_frame',(104,51.5,-4.1),(106,6,2.1),'black',1),
      box('backplate_lower_frame',(104,-51.5,-4.1),(106,6,2.1),'black',1),
      box('backplate_end',(154,0,-4.1),(6,104,2.1),'black',1)]
for row in range(14):
    yy=-44+row*6.75
    for col in range(15):
        xx=57+col*6.3+(row%2)*3.15
        if xx>150:continue
        back.append(ring('honeycomb_cell',(xx,yy,-4.1),4.05,3.3,2.1,'black',6))
for x,y in ((-80,-44),(-80,44),(-1,-26),(45,-26),(-1,26),(45,26)):
    back.append(ring('backplate_screw_bezel',(x,y,-5.24),3.1,1.5,.3,'accent',24))
back.append(text_object('backplate_mark','GEFORCE RTX',(-22,-19,-5.25),10,'label',rotation=(math.pi,0,0)))
for j in range(3):
    stripe=box('backplate_graphic',(-60+j*7,25,-5.23),(1.4,28,.08),'label')
    stripe.rotation_euler.z=-.55
    back.append(stripe)
join('backplate',back,'Backplate · furação, inscrições e colmeia','backplate',explode=(0,0,-58))

screws=[]
for x,y in ((-80,-45),(-80,45),(147,-44),(147,44),(-1,-26),(45,-26),(-1,26),(45,26)):
    zz=33.1 if x in (-80,147) else -5.5
    screws.append(cylinder('screw_head',(x,y,zz),1.9,.95,'steel',24))
    screws.append(box('screw_slot',(x,y,zz+.5),(2.4,.35,.07),'black'))
    screws.append(box('screw_slot',(x,y,zz+.5),(.35,2.4,.07),'black'))
join('fasteners',screws,'Parafusos e fixadores · posição aproximada','hardware',explode=(0,0,165))

# Read the bundled reference images without changing their pixels.
reference_names=[('11_28_12','PCB oblíquo'),('11_28_19','PCB ortográfico'),('11_28_26','Espelho de I/O'),
 ('11_28_33','Backplate interno'),('11_28_38','Espelho montado'),('11_28_47','Carenagem ortográfica'),
 ('11_28_59','Coolers · verso'),('11_29_09','Coolers · frente'),('11_29_15','Carenagem oblíqua'),
 ('11_30_30','Dissipador'),('11_30_43','Placa · verso'),('11_31_01','Backplate externo'),('11_31_34','Conjunto montado')]
references=[]
for index,(stamp,label) in enumerate(reference_names,1):
    target=REFS/f'ref_{index:02d}_{stamp}.png'
    if not target.is_file():
        raise FileNotFoundError(f'Missing bundled reference image: {target.name}')
    references.append({'index':index,'label':label,'file':str(target.relative_to(ASSETS)),
                       'original_name':f'Imagem do ChatGPT 25 de out. de 2026, {stamp}.png',
                       'sha256':hashlib.sha256(target.read_bytes()).hexdigest()})

bounds=[]
for obj in model_objects:
    bounds.extend([obj.matrix_world@v.co for v in obj.data.vertices])
minimum=[min(v[axis] for v in bounds) for axis in range(3)]
maximum=[max(v[axis] for v in bounds) for axis in range(3)]
manifest={'schema_version':1,'name':'GALAX RTX 3060 · reference-guided assembly',
          'coordinate_system':'glTF right handed, Y up, X card length, Z card width',
          'units':'millimetre_visual_approximation','model':'rtx3060.glb',
          'nominal_visual_extent_mm':[250,42,115],
          'bounds_min':[minimum[0],minimum[2],-maximum[1]],
          'bounds_max':[maximum[0],maximum[2],-minimum[1]],
          'parts':parts,'references':references,
          'limitations':[
              'Dimensions and hidden geometry are visual approximations; references contain no calibrated scale.',
              'The supplied references differ in backplate branding/vent patterns and connector details; separate component views guide the reconstruction.',
              'SMD layout is illustrative geometry, not an electrical or manufacturing reconstruction.',
              'Only the GPU component has a localized temperature role. Its maximum has no proven coordinate inside the die.',
              'Seven additional thermal channels remain physically unlocalized; VRAM/VRM/heatsink temperatures are not assigned.',
              'Driver fan indices 0 and 1 are assigned to the left and right visual rotors by convention, not by a verified physical mapping.',
              'NVML fan RPM is driver-reported intended RPM; exact physical tachometer and angular phase are not established.'
          ],
          'fan_mapping':{'physical_mapping_verified':False,'rotor_0_visual':'left rotor viewed from fan face',
                         'rotor_1_visual':'right rotor viewed from fan face','blades_per_rotor':9,
                         'phase_synchronized_to_hardware':False},
          'thermal_mapping':{'gpu_package':'measured GPU component only; no inferred spatial distribution',
                             'unknown_channel_indices':[2,3,4,5,6,7,8],
                             'unknown_parts':['vram','vrm','backplate','heatpipes','heatsink','shroud']}}
(ASSETS/'rtx3060.parts.json').write_text(json.dumps(manifest,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')

# Export only the actual assembly, no cameras, floor, helpers or lights.
bpy.ops.object.select_all(action='DESELECT')
for obj in model_objects: obj.select_set(True)
bpy.context.view_layer.objects.active=model_objects[0]
bpy.ops.export_scene.gltf(filepath=str(ASSETS/'rtx3060.glb'),export_format='GLB',
                          use_selection=True,export_apply=True,export_yup=True,
                          export_extras=True,export_normals=True,export_materials='EXPORT')

# Studio presentation in the .blend is separate from the exported model.
floor=box('studio_floor',(32,0,-10),(700,700,3),'black',2)
world=bpy.data.worlds.new('Studio') if not scene.world else scene.world
scene.world=world
world.use_nodes=True
world.node_tree.nodes['Background'].inputs['Color'].default_value=(.022,.028,.035,1)
world.node_tree.nodes['Background'].inputs['Strength'].default_value=.45
for name,loc,power,size in [('Key',(-110,-80,270),1700,240),('Fill',(160,100,150),1100,200),('Rim',(100,-160,100),1400,180)]:
    data=bpy.data.lights.new(name,'AREA'); data.energy=power*180; data.shape='DISK'; data.size=size
    obj=bpy.data.objects.new(name,data); scene.collection.objects.link(obj); obj.location=loc
    obj.rotation_euler=(Vector((25,0,10))-obj.location).to_track_quat('-Z','Y').to_euler()
camdata=bpy.data.cameras.new('Assembly camera'); camera=bpy.data.objects.new('Assembly camera',camdata)
scene.collection.objects.link(camera); scene.camera=camera
camera.location=(330,-330,285)
camera.rotation_euler=(Vector((30,0,10))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.type='ORTHO';camera.data.ortho_scale=335; camera.data.clip_end=2000
scene.render.engine='CYCLES';scene.cycles.device='CPU';scene.cycles.samples=24
scene.cycles.use_denoising=True
scene.render.threads_mode='FIXED';scene.render.threads=6
scene.render.resolution_x=1500;scene.render.resolution_y=1050;scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG'
scene.render.film_transparent=False
scene.view_settings.view_transform='AgX'
bpy.ops.wm.save_as_mainfile(filepath=str(MODELS/'rtx3060-reference.blend'))
scene.render.filepath=str(MODELS/'assembled-preview.png')
bpy.ops.render.render(write_still=True)

original={obj.name:obj.location.copy() for obj in model_objects}
for obj,part in zip(model_objects,parts):
    e=part['explode']
    obj.location += Vector((e[0],-e[2],e[1]))
floor.hide_render=True
camera.location=(375,-420,340)
camera.rotation_euler=(Vector((25,0,62))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.ortho_scale=620
scene.render.filepath=str(MODELS/'exploded-preview.png')
bpy.ops.render.render(write_still=True)
for obj in model_objects: obj.location=original[obj.name]
camera.location=(330,-330,285)
camera.rotation_euler=(Vector((30,0,10))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.ortho_scale=335
scene.render.filepath=str(MODELS/'assembled-preview.png')
bpy.ops.wm.save_as_mainfile(filepath=str(MODELS/'rtx3060-reference.blend'))
stats={'parts':len(parts),'objects':len(model_objects),'vertices':sum(len(o.data.vertices) for o in model_objects),
       'polygons':sum(len(o.data.polygons) for o in model_objects),'glb_bytes':(ASSETS/'rtx3060.glb').stat().st_size,
       'glb_sha256':hashlib.sha256((ASSETS/'rtx3060.glb').read_bytes()).hexdigest()}
(MODELS/'model-build.json').write_text(json.dumps(stats,indent=2)+'\n',encoding='utf-8')
print('RTX_MODEL_COMPLETE '+json.dumps(stats))
