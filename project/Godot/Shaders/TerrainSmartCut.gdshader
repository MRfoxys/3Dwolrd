shader_type spatial;
render_mode unshaded, cull_back, blend_mix, depth_prepass_alpha;

uniform vec3 u_cam_pos = vec3(0.0, 0.0, 0.0);
uniform vec3 u_focus_pos = vec3(0.0, 0.0, 0.0);
uniform bool u_smart_cut_enabled = false;
uniform float u_cut_radius : hint_range(0.05, 8.0) = 1.35;
uniform float u_cut_fade : hint_range(0.01, 12.0) = 1.25;
uniform float u_alpha_min : hint_range(0.0, 1.0) = 0.2;
uniform vec4 u_fog_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);

varying vec3 v_world_pos;

void vertex()
{
	v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment()
{
	vec3 base_color = COLOR.rgb * u_fog_tint.rgb;
	float alpha = COLOR.a * u_fog_tint.a;

	if (u_smart_cut_enabled)
	{
		vec3 seg = u_focus_pos - u_cam_pos;
		float seg_len2 = dot(seg, seg);
		if (seg_len2 > 1e-6)
		{
			vec3 to_p = v_world_pos - u_cam_pos;
			float t = clamp(dot(to_p, seg) / seg_len2, 0.0, 1.0);
			vec3 closest = u_cam_pos + seg * t;
			float d = distance(v_world_pos, closest);
			float fade = smoothstep(u_cut_radius, u_cut_radius + max(0.001, u_cut_fade), d);
			alpha *= mix(u_alpha_min, 1.0, fade);
		}
	}

	ALBEDO = base_color;
	ALPHA = clamp(alpha, 0.0, 1.0);
}
