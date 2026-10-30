struct Globals {
    view_projection: mat4x4<f32>,
    eye: vec4<f32>,
    thermal: vec4<f32>,
    ambient: vec4<f32>,
};
@group(0) @binding(0) var<uniform> globals: Globals;

struct MeshVertex {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
    @location(3) model0: vec4<f32>,
    @location(4) model1: vec4<f32>,
    @location(5) model2: vec4<f32>,
    @location(6) model3: vec4<f32>,
    @location(7) colour: vec4<f32>,
    @location(8) material: vec4<f32>,
};
struct Surface {
    @builtin(position) position: vec4<f32>,
    @location(0) world: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) colour: vec4<f32>,
    @location(3) material: vec4<f32>,
};
@vertex fn mesh_vs(vertex: MeshVertex) -> Surface {
    let model = mat4x4<f32>(vertex.model0, vertex.model1, vertex.model2, vertex.model3);
    let world = model * vec4<f32>(vertex.position, 1.0);
    var out: Surface;
    out.position = globals.view_projection * world;
    out.world = world.xyz;
    out.normal = normalize((model * vec4<f32>(vertex.normal, 0.0)).xyz);
    out.colour = vertex.colour;
    out.material = vertex.material;
    return out;
}

fn thermal_colour(temperature: f32) -> vec3<f32> {
    let t = clamp((temperature - globals.thermal.y) / (globals.thermal.z - globals.thermal.y), 0.0, 1.0);
    let cool = vec3<f32>(0.055, 0.32, 0.76);
    let middle = vec3<f32>(0.04, 0.72, 0.40);
    let warm = vec3<f32>(1.0, 0.42, 0.035);
    let hot = vec3<f32>(0.95, 0.055, 0.025);
    if (t < 0.35) { return mix(cool, middle, t / 0.35); }
    if (t < 0.70) { return mix(middle, warm, (t - 0.35) / 0.35); }
    return mix(warm, hot, (t - 0.70) / 0.30);
}

// Cook-Torrance microfacets preserve the GLB roughness/metallic distinction:
// fan plastic has broad dielectric highlights; aluminium and copper retain
// coloured reflection. These studio lights are visual, never thermal readings.
fn fresnel_schlick(cosine: f32, f0: vec3<f32>) -> vec3<f32> {
    return f0 + (vec3<f32>(1.0) - f0) * pow(clamp(1.0 - cosine, 0.0, 1.0), 5.0);
}

fn studio_light(normal: vec3<f32>, view: vec3<f32>, light: vec3<f32>, radiance: vec3<f32>, base: vec3<f32>, rough: f32, metal: f32) -> vec3<f32> {
    let nv = max(dot(normal, view), 0.001);
    let nl = max(dot(normal, light), 0.0);
    let halfway = normalize(view + light);
    let nh = max(dot(normal, halfway), 0.0);
    let vh = max(dot(view, halfway), 0.0);
    let alpha = rough * rough;
    let alpha2 = alpha * alpha;
    let denominator = nh * nh * (alpha2 - 1.0) + 1.0;
    let distribution = alpha2 / max(3.14159265 * denominator * denominator, 0.0001);
    let k = (rough + 1.0) * (rough + 1.0) / 8.0;
    let geometry = nv / (nv * (1.0 - k) + k) * nl / (nl * (1.0 - k) + k);
    let fresnel = fresnel_schlick(vh, mix(vec3<f32>(0.04), base, metal));
    let reflection = distribution * geometry * fresnel / max(4.0 * nv * nl, 0.001);
    let diffuse = (vec3<f32>(1.0) - fresnel) * (1.0 - metal) * base / 3.14159265;
    return (diffuse + reflection) * radiance * nl;
}

@fragment fn mesh_fs(in: Surface, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    var normal = normalize(in.normal);
    if (!front) { normal = -normal; }
    let view = normalize(globals.eye.xyz - in.world);
    var base = in.colour.rgb;
    if (in.material.z > 0.5 && globals.thermal.w > 0.5) {
        base = mix(base, thermal_colour(globals.thermal.x), 0.88);
    }
    let rough = clamp(in.material.x, 0.24, 1.0);
    let metal = clamp(in.material.y, 0.0, 1.0);
    let hemi = normal.y * 0.5 + 0.5;
    let environment = mix(vec3<f32>(0.075, 0.080, 0.088), vec3<f32>(0.26, 0.28, 0.31), hemi);
    let grazing = fresnel_schlick(max(dot(normal, view), 0.0), mix(vec3<f32>(0.04), base, metal));
    var colour = base * environment * (1.0 - metal * 0.82);
    colour += grazing * environment * (0.28 + (1.0 - rough) * 0.40);
    colour += studio_light(normal, view, normalize(vec3<f32>(-0.55, 0.85, 0.45)), vec3<f32>(2.85, 2.74, 2.59), base, max(rough, 0.38), metal);
    colour += studio_light(normal, view, normalize(vec3<f32>(0.75, 0.45, 0.20)), vec3<f32>(1.16, 1.26, 1.44), base, max(rough, 0.45), metal);
    colour += studio_light(normal, view, normalize(vec3<f32>(0.10, 0.65, -0.95)), vec3<f32>(1.88, 1.96, 2.12), base, max(rough, 0.42), metal);
    // Gentle highlight compression keeps light metals readable without
    // clipping the black plastic into a glossy white surface.
    colour = colour / (vec3<f32>(1.0) + colour * 0.20);
    let edge = pow(1.0 - abs(dot(normal, view)), 3.0);
    if (in.material.w > 0.5) {
        colour = mix(colour, vec3<f32>(0.12, 0.65, 0.52), 0.23) + edge * vec3<f32>(0.12, 0.4, 0.3);
    }
    return vec4<f32>(clamp(colour, vec3<f32>(0.0), vec3<f32>(1.0)), 1.0);
}

struct Backdrop { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex fn background_vs(@builtin(vertex_index) index: u32) -> Backdrop {
    let points = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    var out: Backdrop;
    out.position = vec4<f32>(points[index], 0.999, 1.0);
    out.uv = points[index] * 0.5 + 0.5;
    return out;
}
@fragment fn background_fs(in: Backdrop) -> @location(0) vec4<f32> {
    let delta = (in.uv - vec2<f32>(0.5, 0.56)) * vec2<f32>(1.0, 1.1);
    let glow = exp(-dot(delta, delta) * 5.8);
    let grid = smoothstep(0.985, 1.0, max(cos(in.uv.x * 110.0), cos(in.uv.y * 78.0))) * 0.0018;
    let colour = mix(vec3<f32>(0.005, 0.009, 0.014), vec3<f32>(0.020, 0.033, 0.040), glow) + vec3<f32>(grid);
    return vec4<f32>(colour, 1.0);
}
