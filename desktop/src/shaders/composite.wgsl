@group(0) @binding(0) var scene: texture_2d<f32>;
@group(0) @binding(1) var scene_sampler: sampler;
struct Vertex { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex fn vs_main(@builtin(vertex_index) index: u32) -> Vertex {
    let points = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    var out: Vertex;
    out.position = vec4<f32>(points[index], 0.0, 1.0);
    out.uv = vec2<f32>((points[index].x + 1.0) * 0.5, 1.0 - (points[index].y + 1.0) * 0.5);
    return out;
}
@fragment fn fs_main(in: Vertex) -> @location(0) vec4<f32> {
    let colour = textureSample(scene, scene_sampler, in.uv);
    // egui normally chooses a gamma-space Unorm framebuffer. The offscreen
    // sRGB texture is sampled in linear space, so this target needs encoding.
    let low = colour.rgb * 12.92;
    let high = 1.055 * pow(max(colour.rgb, vec3<f32>(0.0)), vec3<f32>(1.0 / 2.4)) - 0.055;
    return vec4<f32>(select(high, low, colour.rgb <= vec3<f32>(0.0031308)), colour.a);
}
@fragment fn fs_linear(in: Vertex) -> @location(0) vec4<f32> {
    return textureSample(scene, scene_sampler, in.uv);
}
