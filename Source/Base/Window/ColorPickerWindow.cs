using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ASQPicker;

/// <summary>自带完整布局的取色窗：色轮 + 预设色板 + RGB 文本框 + 透明度滑条。
/// 不继承 Dialog_ColorPickerBase（其读回行为私有绘制），以便给「当前颜色 / 旧颜色」等所有色块加棋盘格透明底纹。</summary>
public class ColorPickerWindow : Window
{
    private const int ContextHash = 195906069; // 布局哈希，任意恒定值即可
    private const float AlphaRowHeight = 30f;
    private static readonly Vector2 ButSize = new Vector2(150f, 38f);

    // 整张贴图的 uv：棋盘格贴图是按色块自身尺寸生成的，永远 1:1 铺满，不平铺。
    private static readonly Rect FullTexCoords = new Rect(0f, 0f, 1f, 1f);

    private readonly Action<Color> onSave;
    private readonly Color oldColor;
    private Color color;
    private float alpha;

    private bool hsvColorWheelDragging;
    private string[] textfieldBuffers = new string[6];
    private Color textfieldColorBuffer;
    private string? previousFocusedControlName;

    // 棋盘格底纹：铺在所有半透明色块下面，让透明效果可见。
    //
    // 做法是「按色块尺寸生成一一对应的贴图 + uv 恒为整张」，刻意不靠 uv 平铺。原因在引擎侧：
    // RimWorld 1.7 的 GUI 后端建采样器时把 U/V/W 全写死成 ClampToEdge、过滤写死成 Linear
    // （见 RimWorld.dll 的 RimWorldWaylith.Imgui.ImGuiVulkanRenderer.CreateSampler），
    // 既不认 wrapMode = Repeat 也不认 filterMode = Point。原来的「8x8 小贴图 + uv 放大到 >1
    // 靠 Repeat 平铺」在 1.7 会变成「左上角一小块棋盘 + 其余区域边缘像素拉伸」的错图。
    // 换成 1:1 贴图后，绘制结果不依赖采样器的任何行为。
    //
    // 格子仍固定 4px：贴图尺寸直接取色块的整数尺寸，格子大小与色块大小无关；右/下边缘
    // 剩不满一格时按原样裁掉——与旧实现靠 Repeat 平铺切出的边缘完全一致。
    // 尺寸取整到整数像素而不是取 4 的倍数：后端会把贴图四角对齐到整数屏幕像素
    // （AddImageRect 里的 Floor(... + 0.51f)），只有贴图与色块尺寸相等时才是 1:1 绘制，
    // 采样恰好落在纹素中心、不发虚；取 4 的倍数会把图案整体缩放几个百分点，反而糊。
    //
    // 代价：每个不同的色块尺寸各缓存一张贴图。色块尺寸来自窗口布局、同一窗口内恒定，
    // 数量有界（取色窗 2 种、投射物覆写窗 1 种），不随帧数或列表行数增长。
    // 已知残留：后端只给 Linear 过滤，且 UI 缩放不是 100% 时色块本身就被放大，4px 格子会随之
    // 略微发虚——采样器在引擎里，mod 侧改不回 Point，这属于 1.7 的限制。
    //
    // 懒加载：Unity 纹理必须在主线程创建，静态初始化时机不可控，首次绘制时再生成。
    private static readonly Dictionary<(int width, int height), Texture2D> alphaCheckerTexes =
        new Dictionary<(int, int), Texture2D>();

    private static Texture2D AlphaCheckerTexFor(Rect rect)
    {
        (int width, int height) key = (CheckerTexSize(rect.width), CheckerTexSize(rect.height));
        if (alphaCheckerTexes.TryGetValue(key, out Texture2D? cached)) return cached;

        Texture2D tex = MakeAlphaCheckerTex(key.width, key.height);
        alphaCheckerTexes[key] = tex;
        return tex;
    }

    // 贴图尺寸 = 色块的整数尺寸（最小 4px，避免退化成 0 尺寸贴图）
    private static int CheckerTexSize(float size)
    {
        return Mathf.Max(4, Mathf.RoundToInt(size));
    }

    private static Texture2D MakeAlphaCheckerTex(int width, int height)
    {
        Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
        tex.name = "GunplayAlphaChecker" + width + "x" + height;
        // 照旧写上（表达意图，也为引擎将来支持时能生效）；当前后端会忽略这两项
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        Color32 dark = new Color32(140, 140, 140, 255);
        Color32 light = new Color32(224, 224, 224, 255);

        // SetPixels32 的数组第 0 行就是 v=0（贴图上"下"），与 SetPixel 的 y 同向，
        // 所以直接按 y 递增写即可（同 TrailTexture 里的约定）。一次性写入而不是逐像素 SetPixel：
        // 大色块（如读回行那种宽条）会有上万个像素。
        Color32[] pixels = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool alt = ((x >> 2) + (y >> 2)) % 2 == 1;
                pixels[y * width + x] = alt ? dark : light;
            }
        }

        tex.SetPixels32(pixels);
        // 建好即定稿、之后只读，可以释放 CPU 侧数据。
        // （与 TrailTexture 恰好相反：那张要反复就地重填，必须保持可读。）
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return tex;
    }

    private static readonly List<Color> pickableColors = new List<Color>
    {
        new Color(0.30f, 0.69f, 0.20f), // 绿
        new Color(0.95f, 0.85f, 0.20f), // 黄
        new Color(0.85f, 0.20f, 0.20f), // 红
        new Color(1.00f, 0.50f, 0.00f), // 橙
        new Color(0.20f, 0.50f, 1.00f), // 蓝
        new Color(0.50f, 0.20f, 0.80f), // 紫
        Color.white,
        new Color(0.55f, 0.55f, 0.55f), // 灰
        Color.black,
        Color.red,
        Color.green,
        Color.blue,
        Color.yellow,
        Color.cyan,
        Color.magenta,
    };

    public override Vector2 InitialSize => new Vector2(600f, 520f);

    public ColorPickerWindow(Color initialColor, Action<Color> onSave)
    {
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = true;
        closeOnAccept = false;
        color = initialColor;
        oldColor = initialColor;
        alpha = initialColor.a;
        this.onSave = onSave;
    }

    public override void DoWindowContents(Rect inRect)
    {
        // 色轮 / 调色板 / 文本框选色都会把 alpha 重置为 1，先把滑条值写回。
        color.a = alpha;

        using (TextBlock.Default())
        {
            RectDivider layout = new RectDivider(inRect, ContextHash);

            // 底部锚定，自下而上堆叠：按钮行 → 透明度行 → 当前/旧颜色读回行。
            RectDivider buttonRow = layout.NewRow(ButSize.y, VerticalJustification.Bottom);
            RectDivider alphaRow = layout.NewRow(AlphaRowHeight + 4f, VerticalJustification.Bottom);
            RectDivider readbackRow = layout.NewRow(Text.LineHeight * 2f + 6f, VerticalJustification.Bottom);

            // 标题
            using (new TextBlock(GameFont.Medium))
            {
                TaggedString title = "ChooseAColor".Translate().CapitalizeFirst();
                RectDivider titleRect = layout.NewRow(Text.CalcHeight(title, layout.Rect.width));
                Rect rect = titleRect.Rect;
                rect.y -= 5f;
                Widgets.Label(rect, title);
            }
            layout.NewRow(0f);

            // 预设色板（右列）与 R/G/B 文本框（左侧），都画在色轮行区域里
            float paletteHeight = DrawColorPalette(layout);
            Vector2 textfieldSize = DrawColorTextfields(layout);

            // 色轮：挖掉左侧文本框列与右侧色板列，画在中间剩余区域（与原版一致，避免压住色板）
            float wheelRowHeight = Mathf.Max(paletteHeight, 128f, textfieldSize.y);
            RectDivider wheelRow = layout.NewRow(wheelRowHeight);
            wheelRow.NewCol(textfieldSize.x);
            wheelRow.NewCol(250f, HorizontalJustification.Right);
            Rect wheelRect = wheelRow.Rect.ContractedBy(
                (wheelRow.Rect.width - 128f) / 2f,
                (wheelRow.Rect.height - 128f) / 2f);
            Widgets.HSVColorWheel(wheelRect, ref color, ref hsvColorWheelDragging);

            // 读回 + 透明度 + 按钮
            DrawReadback(readbackRow);
            DrawAlphaRow(alphaRow);

            if (Widgets.ButtonText(buttonRow.NewCol(ButSize.x), "GunplayPickerCancel".Translate()))
            {
                Close();
            }
            if (Widgets.ButtonText(buttonRow.NewCol(ButSize.x, HorizontalJustification.Right), "GunplayPickerConfirm".Translate()))
            {
                onSave(new Color(color.r, color.g, color.b, alpha));
                Close();
            }

            if (Event.current.type == EventType.Layout)
            {
                previousFocusedControlName = GUI.GetNameOfFocusedControl();
            }
        }
    }

    private float DrawColorPalette(RectDivider layout)
    {
        // layout 是按值传入的 struct 副本，从这里切出右侧色板列不影响外层的布局状态。
        RectDivider paletteCol = layout.NewCol(250f, HorizontalJustification.Right);

        // 默认颜色块
        const float boxSize = 26f;
        RectDivider boxRow = paletteCol.NewRow(boxSize);
        boxRow.Rect.SplitVertically(4f * (boxSize + 2f), out Rect left, out Rect _);
        RectDivider boxLayout = new RectDivider(left, ContextHash, new Vector2(10f, 2f));
        Color defaultColor = oldColor;
        defaultColor.a = 1f;
        Widgets.ColorBox(boxLayout.NewCol(boxSize), ref color, defaultColor);
        Widgets.Label(boxLayout, "Default".Translate().CapitalizeFirst());

        // 预设色
        Widgets.ColorSelector(paletteCol, ref color, pickableColors, out float selectorHeight);
        return selectorHeight + boxSize + 2f;
    }

    private Vector2 DrawColorTextfields(RectDivider layout)
    {
        RectAggregator aggregator = new RectAggregator(new Rect(layout.Rect.position, new Vector2(125f, 0f)), ContextHash);
        Widgets.ColorTextfields(ref aggregator, ref color, ref textfieldBuffers, ref textfieldColorBuffer, previousFocusedControlName, "colorTextfields",
            Widgets.ColorComponents.Red | Widgets.ColorComponents.Green | Widgets.ColorComponents.Blue,
            Widgets.ColorComponents.All);
        return aggregator.Rect.size;
    }

    private void DrawReadback(RectDivider row)
    {
        TaggedString currentLabel = "CurrentColor".Translate().CapitalizeFirst();
        TaggedString oldLabel = "OldColor".Translate().CapitalizeFirst();
        float labelWidth = Mathf.Max(100f, currentLabel.GetWidthCached(), oldLabel.GetWidthCached());

        RectDivider line = row.NewRow(Text.LineHeight);
        Widgets.Label(line.NewCol(labelWidth), currentLabel);
        DrawCheckerSwatch(line.Rect, new Color(color.r, color.g, color.b, alpha));

        line = row.NewRow(Text.LineHeight);
        Widgets.Label(line.NewCol(labelWidth), oldLabel);
        DrawCheckerSwatch(line.Rect, oldColor);
    }

    private void DrawAlphaRow(RectDivider row)
    {
        Rect rect = row.Rect;
        Rect swatchRect = new Rect(rect.x, rect.y + (rect.height - 22f) / 2f, 22f, 22f);
        Rect labelRect = new Rect(swatchRect.xMax + 6f, rect.y, 110f, rect.height);
        Rect sliderRect = new Rect(labelRect.xMax + 8f, rect.y, rect.xMax - labelRect.xMax - 8f, rect.height);

        DrawCheckerSwatch(swatchRect, new Color(color.r, color.g, color.b, alpha));

        using (new TextBlock(TextAnchor.MiddleLeft))
        {
            Widgets.Label(labelRect, "GunplayPickerAlpha".Translate(Mathf.RoundToInt(alpha * 100f)));
        }

        alpha = Widgets.HorizontalSlider(sliderRect, alpha, 0f, 1f, middleAlignment: true, roundTo: 0.01f);
        color.a = alpha;
    }

    /// <summary>带棋盘格底纹的色块：底纹 + 颜色 + 描边（供其他窗口的色块复用）。</summary>
    public static void DrawCheckerSwatch(Rect rect, Color displayed)
    {
        // uv 取整张：贴图尺寸已按 rect 生成，画出即 1:1，不需要 Repeat 平铺（见 alphaCheckerTexes 的注释）
        GUI.DrawTextureWithTexCoords(rect, AlphaCheckerTexFor(rect), FullTexCoords);
        Widgets.DrawBoxSolid(rect, displayed);
        Widgets.DrawBox(rect);
    }

    /// <summary>以 HTML 十六进制字符串形式读写颜色（颜色无法直接用 Scribe_Values 序列化）。
    public static void LookColor(ref Color color, string label, Color defaultColor)
    {
        string text = "#" + ColorUtility.ToHtmlStringRGBA(color);
        Scribe_Values.Look(ref text, label, "#" + ColorUtility.ToHtmlStringRGBA(defaultColor));
        if (ColorUtility.TryParseHtmlString(text, out Color parsed))
        {
            color = parsed;
        }
        else
        {
            color = defaultColor;
        }
    }

    /// <summary>同上，但读写的是「以字符串为键、Color 为值」的字典。
    /// 运行时直接存 Color，只在存取这一刻转成 HTML 字符串中转；解析失败的条目跳过（该键回到默认色）。
    /// 用于 Scribe_Collections 无法直接处理 Color 的场合（如 mod 设置里的 defName → 颜色 表）。
    public static void LookColorDictionary(ref Dictionary<string, Color> colors, string label)
    {
        Dictionary<string, string>? text = null;

        if (Scribe.mode == LoadSaveMode.Saving)
        {
            text = new Dictionary<string, string>(colors?.Count ?? 0);
            if (colors != null)
            {
                foreach (KeyValuePair<string, Color> entry in colors)
                {
                    text[entry.Key] = "#" + ColorUtility.ToHtmlStringRGBA(entry.Value);
                }
            }
        }

        Scribe_Collections.Look(ref text, label, LookMode.Value, LookMode.Value);

        Dictionary<string, Color> result = new Dictionary<string, Color>(text?.Count ?? 0);
        if (text != null)
        {
            foreach (KeyValuePair<string, string> entry in text)
            {
                if (entry.Key != null && ColorUtility.TryParseHtmlString(entry.Value, out Color parsed))
                {
                    result[entry.Key] = parsed;
                }
            }
        }

        colors = result;
    }
}
