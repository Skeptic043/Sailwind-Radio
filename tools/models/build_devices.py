"""Original Sailwind radio family. Blender 4.5 LTS, metres, no external assets.
Run: blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python this.py
Unity coordinates are x/right, y/up, z/back. Blender uses x/right, y/back, z/up.
"""
import bpy, math, json, sys, hashlib
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'assets' / 'runtime'
ART = ROOT / 'assets' / 'authoring'
OUT.mkdir(parents=True, exist_ok=True)
ART.mkdir(parents=True, exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
bpy.context.scene.unit_settings.system='METRIC'
bpy.context.scene.unit_settings.scale_length=1
bpy.context.preferences.filepaths.save_version=0
MATS=[]
def material(name,color,metal=0,rough=.6):
    m=bpy.data.materials.new(name); m.diffuse_color=(*color,1); m.use_nodes=True
    p=m.node_tree.nodes.get('Principled BSDF'); p.inputs['Base Color'].default_value=(*color,1)
    p.inputs['Metallic'].default_value=metal; p.inputs['Roughness'].default_value=rough
    m['metal']=metal; m['rough']=rough; MATS.append(m); return m
WOOD=material('Oiled walnut',(.20,.085,.032),0,.5)
EDGE=material('Walnut end grain',(.115,.04,.015),0,.65)
BRASS=material('Brushed warm brass',(.60,.37,.115),.72,.32)
DARK=material('Charcoal speaker cloth',(.035,.041,.038),0,.95)
RUBBER=material('Rubber and leather',(.018,.022,.020),0,.82)
CONE=material('Paper speaker cones',(.085,.094,.080),0,.8)
IVORY=material('Ivory control inlay',(.80,.75,.54),.1,.4)
SCREEN=material('Recessed amber glass',(.04,.025,.009),0,.42)
OBJECTS=[]; KIND=0; PIVOTS={}
def finish(obj,name,mat,part='body'):
    obj.name=f'{KIND}_{name}'; obj.data.materials.append(mat); obj['kind']=KIND; obj['part']=part
    OBJECTS.append(obj); return obj
def pos(p):return (p[0],p[2],p[1])
def box(name,loc,size,mat,bevel=0,part='body'):
    bpy.ops.mesh.primitive_cube_add(size=1,location=pos(loc));o=bpy.context.object;o.dimensions=pos(size)
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    if bevel:
        b=o.modifiers.new('Authored eased edges','BEVEL');b.width=bevel;b.segments=1
        bpy.context.view_layer.objects.active=o;bpy.ops.object.modifier_apply(modifier=b.name)
    return finish(o,name,mat,part)
def cylinder(name,loc,radius,depth,mat,part='body',verts=16):
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts,radius=radius,depth=depth,location=pos(loc),rotation=(math.pi/2,0,0))
    o=bpy.context.object
    for p in o.data.polygons:p.use_smooth=len(p.vertices)==4
    return finish(o,name,mat,part)
def top_cylinder(name,loc,radius,depth,mat,part='body',verts=16):
    # Blender Z is Unity Y, so an unrotated cylinder faces upward.
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts,radius=radius,depth=depth,location=pos(loc))
    o=bpy.context.object
    return finish(o,name,mat,part)
def ring(name,loc,radius,tube,mat,part='body',major=20):
    bpy.ops.mesh.primitive_torus_add(major_radius=radius,minor_radius=tube,major_segments=major,minor_segments=4,
        location=pos(loc),rotation=(math.pi/2,0,0))
    o=bpy.context.object
    for p in o.data.polygons:p.use_smooth=True
    return finish(o,name,mat,part)
def line(name,a,b,width,mat,part='body'):
    # Flat face strokes need two triangles, not closed eight-sided pipes.
    if abs(a[2]-b[2])<.000001:
        delta=Vector((b[0]-a[0],b[1]-a[1]));delta.normalize()
        perpendicular=Vector((-delta.y,delta.x))*width*.5
        points=[(a[0]+perpendicular.x,a[1]+perpendicular.y,a[2]),
                (a[0]-perpendicular.x,a[1]-perpendicular.y,a[2]),
                (b[0]-perpendicular.x,b[1]-perpendicular.y,b[2]),
                (b[0]+perpendicular.x,b[1]+perpendicular.y,b[2])]
        mesh=bpy.data.meshes.new(name);mesh.from_pydata([pos(p) for p in points],[],[(0,1,2,3)]);mesh.update()
        ob=bpy.data.objects.new(name,mesh);bpy.context.collection.objects.link(ob);return finish(ob,name,mat,part)
    delta=Vector(pos(b))-Vector(pos(a));mid=(Vector(pos(a))+Vector(pos(b)))*.5
    bpy.ops.mesh.primitive_cylinder_add(vertices=8,radius=width*.5,depth=delta.length,location=mid)
    o=bpy.context.object;o.rotation_euler=delta.to_track_quat('Z','Y').to_euler();return finish(o,name,mat,part)
def triangle(name,x,y,z,s,mat,part,left=False):
    points=[(x-s*.5,y-s*.65,z),(x-s*.5,y+s*.65,z),(x+s*.65,y,z)]
    if left:points=[(2*x-a,b,c) for a,b,c in points]
    mesh=bpy.data.meshes.new(name);mesh.from_pydata([pos(p) for p in points],[],[(0,1,2) if left else (0,2,1)]);mesh.update()
    ob=bpy.data.objects.new(name,mesh);bpy.context.collection.objects.link(ob);return finish(ob,name,mat,part)
def icon(name,x,y,z,s):
    part='icon_'+name
    if name=='power':
        # Open ring and vertical stroke, clearly readable without text glyphs.
        for i in range(12):
            a=math.radians(135+i*270/12);b=math.radians(135+(i+1)*270/12)
            line('Power arc',(x+math.cos(a)*s*.5,y+math.sin(a)*s*.5,z),(x+math.cos(b)*s*.5,y+math.sin(b)*s*.5,z),s*.13,IVORY,part)
        line('Power stem',(x,y+s*.12,z),(x,y+s*.75,z),s*.14,IVORY,part)
    elif name=='playpause':
        for dx in (-.19,.19):box('Pause',(x+dx*s,y,z),(s*.2,s*.78,.001),IVORY,part=part)
    elif name in ('previous','next'):
        sign=-1 if name=='previous' else 1
        triangle(name,x,y,z,s*.8,IVORY,part,left=sign<0)
        box('Track end',(x+sign*s*.63,y,z),(s*.13,s*.95,.001),IVORY,part=part)
    elif name=='shuffle':
        for sign in (-1,1):
            line('Shuffle crossing',(x-s*.65,y+sign*s*.42,z),(x+s*.5,y-sign*s*.42,z),s*.11,IVORY,part)
            triangle('Shuffle arrow',x+s*.57,y-sign*s*.42,z,s*.28,IVORY,part)
    elif name=='collections':
        for i in range(3):box('Collection rows',(x,y+(i-1)*s*.35,z),(s*1.05,s*.14,.001),IVORY,part=part)
    elif name in ('master','local','volume','bass'):
        if name=='master':
            for dx,h in [(-.5,.45),(0,.9),(.5,.65)]:box('Master level',(x+dx*s,y,z),(s*.2,s*h,.001),IVORY,part=part)
        elif name=='bass':
            line('Bass wave',(x-s*.6,y,z),(x-s*.3,y-s*.35,z),s*.12,IVORY,part)
            line('Bass wave',(x-s*.3,y-s*.35,z),(x+s*.3,y+s*.35,z),s*.12,IVORY,part)
            line('Bass wave',(x+s*.3,y+s*.35,z),(x+s*.6,y,z),s*.12,IVORY,part)
        else:
            box('Speaker symbol',(x-s*.3,y,z),(s*.32,s*.45,.001),IVORY,part=part)
            triangle('Speaker horn',x+s*.08,y,z,s*.6,IVORY,part,left=True)
def button(name,x,y,z,r):
    cylinder('Button '+name,(x,y,z),r,.012,RUBBER,'body',12)
    cylinder('Button cap '+name,(x,y,z-.008),r*.84,.009,BRASS,'control_'+name,12)
    icon(name,x,y,z-.014,r*.87)
def knob(name,x,y,z,r):
    PIVOTS[(KIND,'control_'+name)]=[x,y,z-.015]
    PIVOTS[(KIND,'indicator_'+name)]=[x,y,z-.015]
    cylinder('Knob base',(x,y,z),r*1.1,.012,RUBBER)
    cylinder('Knob '+name,(x,y,z-.015),r,.026,BRASS,'control_'+name,12)
    box('Knob index',(x,y+r*.55,z-.031),(r*.12,r*.5,.002),IVORY,part='indicator_'+name)
    front=-SIZES[KIND][2] if KIND==1 else -SIZES[KIND][2]*.5
    icon(name,x,y-r*(1.35 if KIND==1 else 1.5),front-.012,r*.65)
def top_knob(name,x,z,r):
    PIVOTS[(KIND,'control_'+name)]=[x,.348,z]
    PIVOTS[(KIND,'indicator_'+name)]=[x,.348,z]
    top_cylinder('Top knob base',(x,.336,z),r*1.1,.012,RUBBER)
    top_cylinder('Top knob '+name,(x,.348,z),r,.018,BRASS,'control_'+name,12)
    box('Top knob index',(x,.358,z-r*.57),(r*.13,.002,r*.48),IVORY,part='indicator_'+name)
    if name=='master':
        for dx,h in [(-.012,.006),(0,.010),(.012,.008)]:
            box('Master level mark',(x+dx,.332,z-.038),(r*.14,.001,h),IVORY,part='body')
    else:
        box('Local level mark',(x,.332,z-.038),(r*.36,.001,.007),IVORY,part='body')
def driver(x,y,z,r,cloth=False):
    cylinder('Driver gasket',(x,y,z),r*1.05,.01,RUBBER)
    cylinder('Woven grille' if cloth else 'Paper cone',(x,y,z-.006),r,.01,DARK if cloth else CONE)
    ring('Driver brass rim',(x,y,z-.012),r,.006 if r>.1 else .0035,BRASS,major=24 if r>.1 else 16)
    if cloth:
        for i in range(-4,5):
            dx=i*r/5;h=math.sqrt(max(0,r*r-dx*dx))*.95
            line('Cloth weave',(x+dx,y-h,z-.014),(x+dx,y+h,z-.014),.0017 if r>.1 else .001,EDGE)
    else:
        ring('Cone suspension',(x,y,z-.014),r*.77,r*.06,RUBBER,major=24)
        cylinder('Dust cap',(x,y,z-.021),r*.32,.014,RUBBER)
    # Brass rim carries the visual fastening detail without sub-pixel screw meshes.

def cabinet(w,h,d,wall=False):
    z=-d*.5 if wall else 0
    shell=box('Rounded walnut cabinet',(0,h*.5,z),(w,h,d),WOOD,.016 if KIND==0 else min(w*.07,.028))
    baffle=box('Inset dark front baffle',(0,h*.5,z-d*.5-.003),(w*.91,h*.89,.015),EDGE,.01)
    if KIND==0:
        # A real opening through the front shell and baffle. The glass sits inside
        # this pocket rather than being a colored plaque attached to the cabinet.
        bpy.ops.mesh.primitive_cube_add(size=1,location=pos((.075,.18,-.101)))
        cutter=bpy.context.object;cutter.dimensions=pos((.346,.164,.054))
        bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
        for surface in (shell,baffle):
            modifier=surface.modifiers.new('Recessed display pocket','BOOLEAN')
            modifier.operation='DIFFERENCE';modifier.object=cutter;modifier.solver='EXACT'
            bpy.context.view_layer.objects.active=surface
            bpy.ops.object.modifier_apply(modifier=modifier.name)
        bpy.data.objects.remove(cutter,do_unlink=True)
    box('Rear inset panel',(0,h*.5,-.004 if wall else z+d*.5+.001),(w*.80,h*.72,.008),EDGE,.008)
    for side in (-1,1):
        rail_x=side*w*.45
        box('Brass corner rail',(rail_x,h*.5,z-d*.5-.012),(.007,h*.8,.007),BRASS)
    if not wall:
        for x in (-w*.32,w*.32):
            for zz in (-d*.30,d*.30):
                if KIND==0:
                    # The radio feet sit below the bottom plane with a small
                    # overlap to hide the seam. The earlier tall blocks were
                    # buried almost entirely inside the cabinet.
                    box('Rubber foot',(x,-.004,zz),(.04,.012,.038),RUBBER)
                else:
                    box('Rubber foot',(x,.014,zz),(.04,.028,.038),RUBBER)
    for i in range(4):box('Rear ventilation',(0,h*(.32+i*.10),-.004 if wall else z+d*.5+.006),(w*.55,.008,.008),RUBBER)

SIZES=[(.6,.38,.2),(.14,.20,.12),(.367,.75,.4),(.9,.9,.75)]
for KIND in range(4):
    w,h,d=SIZES[KIND];cabinet(w,h if KIND else .33,d,KIND==1);front=-d if KIND==1 else -d*.5
    if KIND==0:
        # Raised handle stays within the original .38m overall envelope.
        for x in (-.14,.14):box('Handle brass bracket',(x,.345,0),(.025,.06,.04),BRASS,.01)
        box('Leather carry handle',(0,.367,0),(.29,.026,.035),RUBBER,.012)
        driver(-.185,.18,front-.014,.072,True)
        for side in (-1,1):
            box('Inset display side bezel',(.075+side*.174,.18,-.099),(.012,.164,.012),BRASS,.002)
            box('Inset display top bottom bezel',(.075,.18+side*.076,-.099),(.348,.012,.012),BRASS,.002)
        box('Display glass',(.075,.18,-.096),(.332,.140,.004),SCREEN,.002,part='screen')
        top_knob('master',-.21,-.027,.032)
        top_knob('local',.21,-.027,.032)
        for i,name in enumerate(('previous','playpause','next','shuffle','collections')):
            button(name,-.235+i*.09,.055,front-.021,.029)
        button('power',.228,.058,front-.021,.034)
    elif KIND==1:
        driver(0,.123,front-.012,.05,True)
        button('power',-.034,.041,front-.016,.017)
        knob('volume',.030,.041,front-.016,.016)
        box('Wall mount shoe',(0,.10,-.006),(.065,.115,.012),BRASS,.007)
    elif KIND==2:
        driver(0,.44,front-.014,.145)
        driver(0,.655,front-.014,.047,True)
        for x in (-.084,.084):cylinder('Tuned port',(x,.21,front-.011),.039,.009,RUBBER)
        button('power',-.097,.126,front-.02,.028)
        knob('volume',.093,.126,front-.02,.035)
    else:
        driver(0,.52,front-.018,.33)
        box('Bass reflex aperture',(0,.105,front-.014),(.42,.072,.02),RUBBER,.012)
        button('power',-.31,.16,front-.025,.042)
        knob('bass',.30,.16,front-.025,.05)
        for side in (-1,1):box('Recessed side handle',(side*.449,.59,0),(.012,.055,.24),RUBBER,.02)

def export():
    models=[]
    deps=bpy.context.evaluated_depsgraph_get()
    for kind in range(4):
        groups={};vertex_maps={}
        for obj in OBJECTS:
            if obj['kind']!=kind:continue
            key=(obj['part'],MATS.index(obj.data.materials[0]))
            out=groups.setdefault(key,{'name':key[0],'material':key[1],'pivot':PIVOTS.get((kind,key[0]),[0,0,0]),'vertices':[],'normals':[],'triangles':[]})
            vertex_map=vertex_maps.setdefault(key,{})
            evaluated=obj.evaluated_get(deps);mesh=evaluated.to_mesh();mesh.calc_loop_triangles()
            for tri in mesh.loop_triangles:
                # Axis swap is a reflection. Reversing winding preserves the outward surface.
                for index in (2,1,0):
                    loop=mesh.loops[tri.loops[index]];v=obj.matrix_world@mesh.vertices[loop.vertex_index].co
                    n=obj.matrix_world.to_3x3()@mesh.corner_normals[tri.loops[index]].vector
                    position=(round(v.x,6),round(v.z,6),round(v.y,6))
                    normal=(round(n.x,6),round(n.z,6),round(n.y,6))
                    vertex=(position,normal)
                    if vertex not in vertex_map:
                        vertex_map[vertex]=len(out['vertices'])//3
                        out['vertices'].extend(position)
                        out['normals'].extend(normal)
                    out['triangles'].append(vertex_map[vertex])
            evaluated.to_mesh_clear()
        models.append({'kind':kind,'parts':list(groups.values())})
    payload={'version':1,'materials':[{'name':m.name,'color':list(m.diffuse_color),'metallic':m['metal'],'smoothness':1-m['rough']} for m in MATS], 'models':models}
    (OUT/'devices.json').write_text(json.dumps(payload,separators=(',',':')),encoding='utf-8')
    report={'blender':bpy.app.version_string,'source_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'runtime_sha256':hashlib.sha256((OUT/'devices.json').read_bytes()).hexdigest(),
        'models':[{'kind':m['kind'],'parts':len(m['parts']),'triangles':sum(len(p['triangles'])//3 for p in m['parts'])} for m in models]}
    (ART/'model-report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
export()

# Save original authoring objects at origin in separate collections. Preview duplicates
# use explicit presentation offsets and are excluded from the exported payload.
for kind in range(4):
    coll=bpy.data.collections.new(('Radio','Small Speaker','Speaker','Turbo Wolfer')[kind]);bpy.context.scene.collection.children.link(coll)
    for ob in [o for o in OBJECTS if o['kind']==kind]:
        for old in list(ob.users_collection):old.objects.unlink(ob)
        coll.objects.link(ob)
    coll.hide_render=True

offsets=[(-.79,-.37,0),(-.10,-.95,0),(-.30,.20,0),(.63,.22,0)]
icon_preview=material('Preview powered icon',(.80,.75,.54),.1,.4)
screen_preview=material('Preview powered screen',(.04,.025,.009),0,.42)
text_preview=material('Preview track text',(.78,.35,.075),0,.8)
for mat,color in [(icon_preview,(.9,.43,.06)),
                  (screen_preview,(.045,.018,.0025)),(text_preview,(.78,.35,.075))]:
    node=mat.node_tree.nodes['Principled BSDF']
    node.inputs['Emission Color'].default_value=(*color,1)
    node.inputs['Emission Strength'].default_value=1
for ob in OBJECTS:
    duplicate=ob.copy();duplicate.data=ob.data.copy();bpy.context.scene.collection.objects.link(duplicate)
    duplicate.location+=Vector(offsets[ob['kind']]);duplicate['preview']=True
    part=ob['part']
    # Illustration of enabled power/play/shuffle. Other buttons show their unlit state.
    if part in ('icon_power','icon_playpause','icon_shuffle'):
        duplicate.data.materials[0]=icon_preview
    elif part=='screen':duplicate.data.materials[0]=screen_preview
# Preview uses the exact original bitmap rows from production, not a substitute font.
font_source=(ROOT/'src/Models/DotMatrixFont.cs').read_text()
glyph_data=font_source.split('GlyphData = @"',1)[1].split('";',1)[0]
glyphs={line[0]:[int(line[2+i*2:4+i*2],16) for i in range(7)] for line in glyph_data.splitlines() if line}
glyphs[' ']=[0]*7
bitmap=[0.0]*(640*224*4)
for line_index,text in enumerate(('ACROSS THE BLUE','THE TRADE WINDS','EVENING PASSAGE')):
    pitch=6.2
    left=(640-(len(text)*6-1)*pitch)*.5
    for char_index,character in enumerate(text):
        for row,bits in enumerate(glyphs[character]):
            for column in range(5):
                if not(bits & (1<<(4-column))):continue
                x=left+(char_index*6+column+.5)*pitch;y=168-line_index*56+(3-row)*pitch;radius=pitch*.35
                for py in range(max(0,math.floor(y-radius)),min(223,math.ceil(y+radius))+1):
                    for px in range(max(0,math.floor(x-radius)),min(639,math.ceil(x+radius))+1):
                        if (px+.5-x)**2+(py+.5-y)**2<=radius*radius:
                            offset=(py*640+px)*4;bitmap[offset:offset+4]=[1,1,1,1 if line_index==0 else 210/255]
glyph_image=bpy.data.images.new('Production bitmap preview',width=640,height=224,alpha=True)
glyph_image.pixels=bitmap;glyph_image.pack()
nodes=text_preview.node_tree.nodes;nodes.clear()
output=nodes.new('ShaderNodeOutputMaterial');mix=nodes.new('ShaderNodeMixShader')
transparent=nodes.new('ShaderNodeBsdfTransparent');emission=nodes.new('ShaderNodeEmission')
emission.inputs['Color'].default_value=(.78,.35,.075,1)
sample=nodes.new('ShaderNodeTexImage');sample.image=glyph_image;sample.interpolation='Closest'
links=text_preview.node_tree.links
links.new(sample.outputs['Alpha'],mix.inputs[0]);links.new(transparent.outputs[0],mix.inputs[1])
links.new(emission.outputs[0],mix.inputs[2]);links.new(mix.outputs[0],output.inputs[0])
mesh=bpy.data.meshes.new('Preview glyph quad')
mesh.from_pydata([pos((-.091,.11,-.099)),pos((-.091,.25,-.099)),pos((.241,.25,-.099)),pos((.241,.11,-.099))],[],[(0,3,2,1)])
mesh.uv_layers.new(name='UVMap')
uvs=[(0,0),(0,1),(1,1),(1,0)]
for loop in mesh.loops:mesh.uv_layers.active.data[loop.index].uv=uvs[loop.vertex_index]
quad=bpy.data.objects.new('Preview original bitmap display',mesh);bpy.context.scene.collection.objects.link(quad)
quad.location=Vector(offsets[0]);quad['preview']=True;mesh.materials.append(text_preview)
bpy.ops.mesh.primitive_plane_add(size=200,location=(0,0,-.015));ground=bpy.context.object
ground.data.materials.append(material('Preview charcoal',(.032,.045,.043),0,.86))
world=bpy.context.scene.world;world.use_nodes=True;world.node_tree.nodes['Background'].inputs[0].default_value=(.14,.18,.20,1);world.node_tree.nodes['Background'].inputs[1].default_value=.5
for location,power,size in [((-3,-4,5),650,4),((3,-1,4),420,3),((0,4,4),900,3)]:
    bpy.ops.object.light_add(type='AREA',location=location);light=bpy.context.object;light.data.energy=power;light.data.shape='DISK';light.data.size=size
    light.rotation_euler=(Vector((0,0,.3))-light.location).to_track_quat('-Z','Y').to_euler()
bpy.ops.object.camera_add(location=(2.3,-4.4,2.45));camera=bpy.context.object
camera.rotation_euler=(Vector((0,-.12,.32))-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.type='ORTHO';camera.data.ortho_scale=3.0
scene=bpy.context.scene;scene.camera=camera;scene.render.engine='CYCLES';scene.cycles.samples=40
scene.render.resolution_x=1600;scene.render.resolution_y=1100;scene.render.resolution_percentage=100
scene.view_settings.view_transform='AgX';scene.render.image_settings.file_format='PNG';scene.render.filepath=str(ART/'device-family.png')
bpy.ops.wm.save_as_mainfile(filepath=str(ART/'devices.blend'))
bpy.ops.render.render(write_still=True)
camera.location=Vector(offsets[0])+Vector((.38,-1.1,.55))
camera.rotation_euler=(Vector(offsets[0])+Vector((0,-.05,.20))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.ortho_scale=.78
scene.render.resolution_x=1400;scene.render.resolution_y=1000
scene.render.filepath=str(ART/'radio-closeup.png')
bpy.ops.render.render(write_still=True)
for light in bpy.data.lights:light.energy*=.015
world.node_tree.nodes['Background'].inputs[1].default_value=.003
scene.cycles.max_bounces=0
scene.render.filepath=str(ART/'radio-night.png')
bpy.ops.render.render(write_still=True)
print('RADIO_MODELS_EXPORTED',str(OUT/'devices.json'))
