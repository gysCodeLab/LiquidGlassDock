// Liquid Glass material shader.
// One composite pass does everything: SDF shape -> normal -> edge lens ->
// dual sharp/blurred refraction sampling -> grading -> Fresnel rim -> specular
// edge -> drop shadow. No bitmap overlays, no pre-baked gradients.
//
// This file must stay pure ASCII: fxc marshals the source as ANSI and any
// non-ASCII comment breaks compilation on CJK code pages.

cbuffer GlassParams : register(b0)
{
    float4 PanelRect;   // xy = panel top-left in window px, zw = size
    float4 ScreenMap;   // xy = desktop sample offset px, zw = 1 / sample size
    float4 Material;    // x = blur step (texels), y = tint, z = exposure, w = saturation
    float4 Optics;      // x = refraction band px, y = displacement px, z = dispersion, w = bulge
    float4 Rim;         // x = rim intensity, y = rim width px, z = top gloss, w = shadow
    float4 Shape;       // x = corner radius, y = shadow extent, z/w = window size
    float4 Extra;       // x = time, y = dpi, z = has backdrop, w = contrast
    float4 BodyColor;   // rgb = body tint, a = body mix
    float4 Material2;   // x = scatter, y = milk, z = rolloff knee, w = edge specular
    float4 Transmission; // x = clarity 0..1 (0 = full frost, 1 = clearest)
                         // y = alpha pull, z = border weight (0 = no rim / no inner band)
                         // w = 整屏环境亮度 0..1（0 = 未提供；大面板的背景压平靠它门控）
    float4 Fx;           // x = edge fade px (0 = hard cut), y = wall thickness weight,
                         // z = bright-wall smoke (0 = plain white glass, 1 = full smoked body)
    float4 Recovery;
};

Texture2D Backdrop : register(t0);
// t1 carries the sharp refraction layer during the glass pass and the icon
// atlas during the icon pass. Same register on purpose: never bound together.
Texture2D Aux : register(t1);
SamplerState LinearClamp : register(s0);

float sdRoundBox(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// Gradient of the SDF equals the outward normal. Used by refraction and rim.
float2 sdRoundBoxGradient(float2 p, float2 b, float r)
{
    float2 corner = abs(p) - (b - r);
    if (corner.x >= 0.0 || corner.y >= 0.0)
    {
        float2 outside = max(corner, 0.0);
        float lenSq = max(dot(outside, outside), 1e-6);
        return sign(p) * (outside * rsqrt(lenSq));
    }
    float gx = step(corner.y, corner.x);
    return sign(p) * float2(gx, 1.0 - gx);
}

// Lens curve: linear distance in, steep near the rim, C1 continuous at both ends.
float circleMap(float v)
{
    v = saturate(v);
    return 1.0 - sqrt(max(1.0 - v * v, 0.0));
}

float lumaOf(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// Soft knee instead of a hard clip. A bright wall keeps its gradient instead of
// collapsing into a flat white slab, which is what real thick glass does.
float3 RollOff(float3 c, float knee)
{
    float3 over = max(c - knee, 0.0);
    float3 m = over / max(1.0 - knee, 1e-3);
    float3 compressed = knee + (1.0 - knee) * (1.0 - exp(-m));
    return lerp(c, compressed, smoothstep(0.0, 0.015, over));
}

struct QuadOut
{
    float4 pos : SV_Position;
};

QuadOut VS_Quad(uint id : SV_VertexID)
{
    float2 uv = float2((id << 1) & 2, id & 2);
    QuadOut o;
    o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float4 PS_Copy(QuadOut i) : SV_Target
{
    float2 uv = i.pos.xy * ScreenMap.zw;
    return float4(Backdrop.SampleLevel(LinearClamp, uv, 0.0).rgb, 1.0);
}

// 9-tap gaussian, run at reduced resolution: the dock is a thin strip,
// upsampling afterwards is free and hides the low tap count.
float4 PS_BlurH(QuadOut i) : SV_Target
{
    float2 uv = i.pos.xy * ScreenMap.zw;
    float2 texel = ScreenMap.zw * Material.x;
    float w[5] = { 0.2270270270, 0.1945945946, 0.1216216216, 0.0540540541, 0.0162162162 };
    float3 sum = Backdrop.SampleLevel(LinearClamp, uv, 0.0).rgb * w[0];
    [unroll]
    for (int k = 1; k < 5; ++k)
    {
        sum += Backdrop.SampleLevel(LinearClamp, uv + float2(texel.x * k, 0.0), 0.0).rgb * w[k];
        sum += Backdrop.SampleLevel(LinearClamp, uv - float2(texel.x * k, 0.0), 0.0).rgb * w[k];
    }
    return float4(sum, 1.0);
}

float4 PS_BlurV(QuadOut i) : SV_Target
{
    float2 uv = i.pos.xy * ScreenMap.zw;
    float2 texel = ScreenMap.zw * Material.x;
    float w[5] = { 0.2270270270, 0.1945945946, 0.1216216216, 0.0540540541, 0.0162162162 };
    float3 sum = Backdrop.SampleLevel(LinearClamp, uv, 0.0).rgb * w[0];
    [unroll]
    for (int k = 1; k < 5; ++k)
    {
        sum += Backdrop.SampleLevel(LinearClamp, uv + float2(0.0, texel.y * k), 0.0).rgb * w[k];
        sum += Backdrop.SampleLevel(LinearClamp, uv - float2(0.0, texel.y * k), 0.0).rgb * w[k];
    }
    return float4(sum, 1.0);
}

// Chromatic dispersion: each channel samples the displaced point at a slightly
// different radius, exactly like a real lens on the rim band.
float3 LensSampleBlur(float2 uv, float2 off, float disp)
{
    float3 c;
    c.r = Backdrop.SampleLevel(LinearClamp, uv + off * (1.0 + disp), 0.0).r;
    c.g = Backdrop.SampleLevel(LinearClamp, uv + off, 0.0).g;
    c.b = Backdrop.SampleLevel(LinearClamp, uv + off * (1.0 - disp), 0.0).b;
    return c;
}

float3 LensSampleSharp(float2 uv, float2 off, float disp)
{
    float3 c;
    c.r = Aux.SampleLevel(LinearClamp, uv + off * (1.0 + disp), 0.0).r;
    c.g = Aux.SampleLevel(LinearClamp, uv + off, 0.0).g;
    c.b = Aux.SampleLevel(LinearClamp, uv + off * (1.0 - disp), 0.0).b;
    return c;
}

float4 PS_Glass(QuadOut i) : SV_Target
{
    float2 px = i.pos.xy;
    float2 halfSize = PanelRect.zw * 0.5;
    float2 center = PanelRect.xy + halfSize;
    float2 p = px - center;
    float radius = min(Shape.x, min(halfSize.x, halfSize.y));

    float sd = sdRoundBox(p, halfSize, radius);

    // No desktop sample is displayed while Windows is still unlocking.
    // The center passes through exactly; only the thin bevel is rendered.
    if (Recovery.x > 0.5)
    {
        float coverage = saturate(0.5 - sd);
        float depth = max(-sd, 0.0) / max(Extra.y, 0.5);
        float2 normal = sdRoundBoxGradient(p, halfSize, radius);
        float bevel = 1.0 - smoothstep(0.25, 2.5, depth);
        float light = saturate(dot(normal, float2(-0.45, -0.893)));
        float highlight = 0.25 * light;
        float shade = 0.15 * (1.0 - light);
        return float4(highlight.xxx, highlight + shade) * bevel * coverage;
    }

    // ---------- outside: soft drop shadow straight from the SDF ----------
    if (sd > 0.0)
    {
        // 白纸上没有浓影。这圈黑色 alpha 由 DWM 直接混到壁纸上，在纯白背景上
        // 就是一圈三十像素宽的脏灰 —— 面板主体再准，外圈一灰照样"太假"。
        //
        // 留多少跟材质走：白玻璃（smoke=0）几乎不留，白纸上的一圈灰晕是最显眼
        // 的假；烟熏暗体（smoke=1）反而需要一层落影接地，否则像块贴纸。
        float sl = lumaOf(Backdrop.SampleLevel(LinearClamp, (px + ScreenMap.xy) * ScreenMap.zw, 0.0).rgb);
        float brightShadow = smoothstep(0.55, 0.92, sl) * step(0.5, Extra.z);
        float t = saturate(sd / max(Shape.y, 1.0));
        float s = pow(1.0 - t, 2.6);
        // 影子还得往下倒。SDF 的影子是各向同性的，落在白纸上就是一圈均匀灰晕；
        // 而真实的落影在面板上方几乎没有、下方才有厚度。按屏幕纵向位置给一个权重，
        // 上缘收到 0.45、下缘放到 1.25 —— 那一圈"脏"就变成了"贴在地上"。
        float below = saturate((px.y - PanelRect.y) / max(PanelRect.w, 1.0));
        float falloff = lerp(0.45, 1.25, smoothstep(0.15, 0.95, below));
        float shadowW = lerp(1.0, falloff, brightShadow);
        // 白玻璃只留一成，够"离纸"就行；烟熏留四成。
        float keep = lerp(0.10, 0.40, saturate(Fx.z));
        return float4(0.0, 0.0, 0.0, s * Rim.w * 0.83 * lerp(1.0, keep, brightShadow) * shadowW);
    }

    float cover = saturate(0.5 - sd);

    // ---------- clarity ----------
    // One knob drives every effect that makes the pane read as "solid frosted"
    // rather than "clear": the veil over dark backdrops, the milky white wash,
    // the cool cast, the inner dark band, and the final alpha. Tuning them
    // separately always produced a material that was clear in one place and
    // chalky in another; they have to move together or it stops looking real.
    float tr = saturate(Transmission.x);

    // ---------- edge scale ----------
    // This material was tuned against an 80px-tall dock strip: a 22px lens band,
    // a 12px rim and a 5.5px inner dark line. Those are absolute pixel counts, so
    // on a 900px card the same numbers cover 5% of the height instead of 27% --
    // the pane stops reading as thick glass and turns into a blurred dark sheet.
    // Scaling by the square root of height keeps the dock at exactly 1.0 (nothing
    // changes there) while a full-screen card lands around 2.8.
    // 用逻辑高度而不是物理像素：否则 125%/150% 缩放下同一个面板会算出不同的
    // 边缘比例，材质随 DPI 漂移。Extra.y 是 DpiScale（Dock 与启动台都会传）。
    float logicalH = PanelRect.w / max(Extra.y, 0.5);
    // max(), not saturate(): saturate clamps the ratio to 1.0, which silently
    // pinned edgeK at 1 for every large panel -- the scaling below was dead code.
    //
    // 上限 1.15：这套边缘结构是按 80px 高的 Dock 标定的（边光 11px、镜面线
    // 1.8px、倒角暗带 8.5px）。原来按 pow(h/80, 0.45) 一路放大，820px 的启动台
    // 落到 2.85 —— 同一圈"亮-暗-亮"变成 25px 宽、暗带铺到 24px，读起来已经不是
    // 玻璃壁，而是有人拿笔在面板上描了一个框（用户原话："还有个边框包裹着"）。
    // 玻璃壁的厚度是绝对量，跟面板多大无关，所以这里钳住：两处边缘同宽。
    float sizeK = max(logicalH / 80.0, 1.0);
    float edgeK = min(pow(sizeK, 0.45), 1.15);

    // ---------- edge lens ----------
    // The band must never swallow a thin panel: cap it against the half height
    // so the frost still has a soft middle on a dock-sized strip.
    float band = max(min(Optics.x, halfSize.y * 0.55), 1.0);
    float f = 1.0 - saturate(-sd / band);
    float lens = f*f*(1.0+.35*f);
    // Displacement uses the full lens; the sharp layer is confined to the wall
    // itself, otherwise a small panel is sharp edge to edge and reads as plastic.
    float wall = lens * lens * lens * lens;

    float2 n = sdRoundBoxGradient(p, halfSize, radius);
    float2 radial = normalize(p + float2(1e-4, 1e-4));
    n = normalize(n + radial * .025);

    float2 uv = (px + ScreenMap.xy) * ScreenMap.zw;
    // Continuous lens slope, without ripples or repeated image contours.
    float2 off = -n * lens * Optics.y * ScreenMap.zw;
    // Dispersion is a fraction of the displacement, not an absolute distance:
    // 0.004 of 12px would be a tenth of a pixel and therefore invisible.
    // Keep colour fringing at the very edge, rather than across the lens.
    float disp = Optics.z * 0.008 * wall;

    // ---------- backdrop, two ways ----------
    float3 c;
    if (Extra.z < 0.5)
    {
        // No desktop sample available: neutral frosted body, never a black hole.
        // 必须是中性灰。原来那版 (0.44,0.47,0.54) 蓝通道高出 0.10，
        // 一旦采集失效、面板整块都是这个颜色，看起来就是"玻璃变成了蓝色"。
        float g = saturate((px.y - PanelRect.y) / max(PanelRect.w, 1.0));
        c = lerp(float3(0.47, 0.47, 0.475), float3(0.27, 0.27, 0.275), g);
    }
    else
    {
        float3 cBlur = LensSampleBlur(uv, off, disp);
        float3 cSharp = LensSampleSharp(uv, off, disp);
        // iOS 26 keeps the subject readable and concentrates the lens in the
        // wall. A fully blurred centre reads as frosted plastic, not liquid glass.
        float sharpCore = lerp(0.18, 0.52, tr);
        c = lerp(cBlur, cSharp, saturate(sharpCore + wall * (1.0 - sharpCore)));
    }

    // ---------- grading ----------
    float luma = lumaOf(c);
    float mx = max(max(c.r, c.g), c.b);
    float mn = min(min(c.r, c.g), c.b);
    float chroma = (mx - mn) / max(mx, 1e-3);

    c = lerp(luma.xxx, c, Material.w);
    c = (c - 0.5) * Extra.w + 0.5;
    c *= (1.0 + Material.z);

    // Over a dark wall the frost has to lift its own shadows, otherwise the
    // pane reads as a hole. The lift is additive so the backdrop hue survives.
    //
    // 只在**有真实背景**时才加：这层光的色相是 (0.300, 0.315, 0.360)，
    // 蓝通道比红通道高 0.06 —— 叠在中性灰上正好是"玻璃变成蓝色"的来源。
    // 没有采集帧时那身灰本来就是被逼出来的近似，再染一层冷色就彻底露馅了。
    float hasBackdrop = step(0.5, Extra.z);
    float dark = smoothstep(0.42, 0.02, luma);
    c += float3(0.300, 0.315, 0.360) * (dark * Material2.x * (1.0 - 0.82 * tr) * hasBackdrop);

    // A milky veil is only correct over a white or grey wall. Applying it by
    // luminance alone turns a saturated colour into dirty pastel, which is the
    // single biggest giveaway that the material is fake.
    float achromatic = 1.0 - smoothstep(0.10, 0.40, chroma);
    float white = smoothstep(0.62, 1.0, luma) * achromatic;

    // How close the backdrop itself is to paper. Everything below the "light"
    // section is an ADDITION, and adding white to white is a no-op -- so on a
    // near-white wall the material needs a different treatment, not a stronger
    // version of the same one. See the bright-wall block further down.
    float brightW = smoothstep(0.55, 0.90, luma) * achromatic;

    // ---------- large-pane backdrop flattening (Fx.w) ----------
    // 启动台铺满整屏，透的就是"用户此刻所在的那个页面"：壁纸 + 桌面图标 +
    // 开着的窗口。窗口和图标被模糊摊开之后会混成一层中灰 —— 用户说"启动台发灰"，
    // 但真正发灰的不是玻璃，是背景的对比度被糊进了玻璃里。Dock 背后只有一条
    // 空壁纸，所以它白得干净。
    //
    // 真磨砂玻璃的做法不是加白（加白 = 一块白板，什么都透不出来），而是把背景
    // 收成"环境亮度 + 一点隐约的轮廓"：亮度锚到环境白，对比度只留小半。抬的是
    // 暗部，亮部几乎不动，所以面板里仍然看得出后面是深是浅，但不会摊一块脏灰。
    //
    // Dock / 胶囊传 0，一个像素都不受影响。
    //
    // 门控用**整屏环境亮度**（Transmission.w）而不是当前像素的 luma：要修的恰恰
    // 就是"壁纸是白的、桌面上压着一块深窗口"这种情况，按像素判断的话最该抬的
    // 地方恰好被跳过。环境暗（暗色壁纸）时整条失效，暗色模式一点不受影响。
    float liftW = saturate(Fx.w) * smoothstep(0.40, 0.70, saturate(Transmission.w));
    if (liftW > 0.001)
    {
        // 环境白带一点冷：磨砂玻璃透的是天光，不是画纸。
        float3 env = float3(0.980, 0.985, 0.994);
        // 只抬暗部：越暗抬得越多，已经在 env 附近的几乎不动。深窗口抬到 ~0.89，
        // 白区留在 ~0.99 —— 面板整体亮而透，同时仍看得出"这后面有一块东西"。
        c = lerp(c, env, liftW * 0.97 * (1.0 - 0.25 * saturate(lumaOf(c))));
    }

    // Kept deliberately weak: at full strength a light-grey window behind the
    // dock turned the whole pane into an opaque white slab with no trace of the
    // backdrop left in it.
    // And it is pointless on an already-white wall: pushing 0.95 toward 0.96
    // only erases the last bit of structure the backdrop had left.
    c = lerp(c, float3(0.96, 0.975, 1.00),
             white * Material2.y * (1.0 - 0.80 * tr) * 0.55 * (1.0 - brightW));
    c = lerp(c, BodyColor.rgb * 0.72 + 0.14, saturate(Material.y * 0.25 * (1.0 - 0.75 * tr)));

    // A hair of cool bias. Pure neutral white reads as paper; real glass always
    // takes a slight blue cast because the light it transmits is skylit.
    // 降级（没有背景可采）时这层冷偏也收一半：那时画面里只剩灰，
    // 任何一点蓝都会被读成"程序坏了"，而不是"玻璃透光"。
    // 白底上这层冷偏要收掉大半：白 + 蓝 = 蓝灰。用户说的"太灰"里有一份来自这里。
    c = lerp(c, float3(0.93, 0.95, 0.99),
             0.055 * (1.0 - 0.72 * tr) * (0.35 + 0.65 * hasBackdrop) * (1.0 - 0.75 * brightW));

    // A rolloff knee that sits too low flattens the whole top of the range and
    // the pane turns into a white slab on bright backdrops. Keep it high enough
    // that only genuine near-white content gets compressed.
    //
    // 白底是一个特例，必须把 knee 抬到接近 1：0.85 的拐点会把纯白壁纸压到
    // 0.945，面板主体于是从 255 掉到 232 —— 用户看到的就是"灰卡片"。
    // 压缩在这里唯一的作用是防止高光溢出，不该拿来削平一块本来就不该被削的背景。
    float knee = clamp(Material2.z, 0.05, 0.98);
    c = RollOff(c, lerp(knee, 0.965, brightW));

    // ---------- bright wall: smoked, not whitened ----------
    // 亮底上要同时成立三件事：白字看得清、边框看得清、看着还是玻璃。
    // 前两条要求面板比纸暗，第三条要求它是"透"的而不是涂上去的 —— 只有一个解：
    // 把背景压成一层薄的有色玻璃，而不是把它推得更白。
    //
    // 乘法项 0.14 保住背景的明暗与色相（浅蓝白的墙会带一点蓝，不是死灰），
    // 加性项 0.05 才是那层"烟"。纯白上落到 0.19，配 alpha 0.945 之后约 0.24，
    // 白字的对比度足够；同时烟里仍能看见桌面隐约的层次，不像一块塑料板。
    //
    // 之前那版走的是相反的路（本体只比纸暗 1%）—— 面板确实"不灰"了，
    // 但白字压上去一个字都看不见，用户的原话是"既能看清白色字体，也要有玻璃效果"。
    // Only paper-white walls need the smoked treatment. Using brightW here also
    // smoked blue-grey skies and turned them into flat dark slabs.
    float paperW = smoothstep(0.80, 0.96, luma) * achromatic;
    float smokeW = saturate(Fx.z) * paperW;
    float vertical = saturate((px.y - PanelRect.y) / max(PanelRect.w, 1.0));
    float3 smokeTone = c * 0.14 + float3(0.048, 0.054, 0.072);
    c = lerp(c, smokeTone, smokeW);

    // 暗体上的立体感只能靠加法：0.2 乘 1.055 还是 0.21，看不出来。
    // 上缘吃光、下缘沉下去，这一道 6% 的差就是"一块有厚度的料"和"一张灰卡"的分界；
    // 少了它，面板再准也只是个圆角矩形。
    c += float3(0.052, 0.056, 0.064) * (1.0 - vertical) * smokeW;
    c -= float3(0.026, 0.026, 0.024) * vertical * smokeW;

    float borderW=saturate(Transmission.z);
    float depth=max(-sd,0)/max(Extra.y,.5);
    float2 pos=p/max(halfSize,1);
    float bevel=exp(-depth/3.3);
    float fres=bevel*bevel;
    float edge=exp(-depth/.80);
    float2 ray=uv+off;
    float3 through=lerp(Backdrop.SampleLevel(LinearClamp,ray,0).rgb,
                        Aux.SampleLevel(LinearClamp,ray,0).rgb,.29+.60*bevel);
    float3 surround=Backdrop.SampleLevel(LinearClamp,uv+n*ScreenMap.zw*22,0).rgb;
    c=Extra.z>.5 ? through : c;
    float lum=lumaOf(c);
    float key=pow(saturate(dot(n,normalize(float2(-.5,-.866)))),2);
    float glint=key*exp(-pow((pos.x+.55)/.49,2))+ .52*(1-key)*exp(-pow((pos.x-.73)/.33,2));
    float glaze=.034+.032*(1-pos.y)*.5;
    c=c*(.95-.033*saturate(pos.y)) + glaze*(1-lum);
        float paper=smoothstep(.77,.97,lumaOf(surround));
    c=lerp(c,through*.982+.006,paper*.93);
    c=lerp(c,surround,bevel*.18*borderW);
    c*=1-bevel*(.055+.18*lum)*(1-glint)*borderW;
    c=lerp(c,float3(1,1,1),borderW*glint*(edge*.62+bevel*.09));
    // Frost is opaque by nature: the see-through impression comes from the
    // blurred colour underneath, not from letting the sharp desktop leak
    // through. This is not a taste call -- the pane is a translucent layer, so
    // DWM re-composites the *sharp* desktop behind it at (1 - alpha). At 0.865
    // stars and hairlines showed straight through the frost; even 0.925 left
    // 7% sharp backdrop, enough to see a star. Real frosted glass scatters the
    // image away entirely, so the blurred tap has to replace it almost fully.
    //
    // Clarity trades that back deliberately: the lower the alpha, the more of
    // the *sharp* backdrop DWM blends in. That is the only lever that makes a
    // pane look like clear glass instead of frosted plastic, so it is exposed
    // as one knob rather than hidden. 0.972 = fully frosted, 0.790 = clear.
    // The clear end used to sit at 0.880 (12% sharp backdrop). On a busy
    // wallpaper that still read as frosted Perspex, so the knob now opens to
    // 0.790 (21% sharp) -- enough for the wallpaper's structure to survive
    // rather than just its average colour.
    // No size-dependent alpha any more: a big pane now uses exactly the dock's
    // numbers. The reason a big pane used to need more opacity was the sharp
    // leak showing desktop icons and window titles through the card -- but that
    // is a content problem, and it is fixed at the content: the launchpad
    // composites the wallpaper instead of the live desktop, and hides the
    // desktop icons while it is open. With the same wallpaper behind both, the
    // 21% sharp backdrop is the same 21% the dock gets, at any panel size.
    // (Two earlier attempts got this backwards: pushing alpha to 0.97 turned
    // the card into a painted board, and it still ghosted, because alpha is
    // not what keeps icons out -- removing them from the backdrop is.)
    float alphaBase = .97;
    // fres / edge also thicken the alpha right at the wall, which is half of why
    // the border reads as a drawn frame. Same border weight as the light.
    float alpha = cover * saturate(alphaBase
        + (fres * 0.015 + edge * 0.008) * borderW
        + BodyColor.a * 0.15);
    alpha = min(alpha, .992);

    // AlphaPull: pull the pane toward opaque without touching the backdrop.
    // DWM blends the *sharp* content behind the window at (1 - alpha); on the
    // dock that is bare wallpaper, on a full-screen panel it is other people's
    // windows and the desktop icon grid. Clamping the leak leaves the blurred
    // (and on the launchpad, wallpaper-only) backdrop as the only thing visible,
    // so the pane keeps its clarity while losing the ghosts.
    alpha = lerp(alpha, 1.0, saturate(Transmission.y));

    // 烟熏之后不能让 DWM 把背后那张锐利的白纸再混回来 21%：那等于往烟里兑白水，
    // 面板立刻从 0.24 抬到 0.40，白字又开始糊、边缘又开始灰。
    // 亮底把 alpha 拉到接近不透明，通透感交给上面那层"背景 ×0.14"的结构，
    // 不再靠 DWM 的漏光 —— 白底上本来也没有东西可漏。
    alpha = lerp(alpha, 0.945, brightW * saturate(Fx.z));

    // Edge fade: let the pane dissolve into the desktop over the last N pixels
    // instead of ending on a cut line. A hard SDF edge is what makes a big
    // rounded rectangle read as a card lying on top of the screen; a short fade
    // removes that "stuck-on" feel without touching the interior at all.
    float fadeW = Fx.x;
    if (fadeW > 0.5)
    {
        alpha *= smoothstep(0.0, fadeW, -sd);
    }

    // 最后这道滚降在白底上也要抬：0.92 会把刚做出来的高光线一并压平，
    // 白底玻璃的最后一点锐度就没了。
    c = saturate(RollOff(c, lerp(0.92, 0.955, brightW)));
    // alpha already contains cover; multiplying it twice darkens the antialiased rim.
    return float4(c * alpha, alpha);
}

struct IconIn
{
    float2 pos : POSITION;
    float2 uv  : TEXCOORD0;
    float4 col : COLOR;
};

struct IconOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
    float4 col : COLOR;
};

IconOut VS_Icon(IconIn v)
{
    IconOut o;
    float2 ndc = float2(v.pos.x / Shape.z * 2.0 - 1.0, 1.0 - v.pos.y / Shape.w * 2.0);
    o.pos = float4(ndc, 0.0, 1.0);
    o.uv = v.uv;
    o.col = v.col;
    return o;
}

float4 PS_Icon(IconOut i) : SV_Target
{
    float4 t = Aux.Sample(LinearClamp, i.uv);
    return t * i.col;   // texture is premultiplied at upload time
}
