using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ASQPicker;

/// <summary>
/// 独立单文件 Def 选择窗口：仅依赖游戏本体程序集（UnityEngine / Verse / RimWorld），
/// 不依赖任何 Mod 自有代码。把本文件整体复制到其它项目即可使用（可按需修改命名空间）。
/// 用法：RimWorldDefPicker.DefSelectionWindow.Open("标题", defs, current, def => { ... });
/// </summary>
public class DefSelectionWindow : Window
{
    // ===== 可本地化文本（公共字段，可直接修改或接入翻译系统） =====
    public string ConfirmButtonText = "确定";
    public string CancelButtonText = "取消";
    public string ClearButtonText = "清除";
    public string CopyButtonText = "复制";
    public string PasteButtonText = "粘贴";
    public string CurrentText = "当前";
    public string NoneText = "无";
    public string MiscGroupText = "其他";

    /// <summary>窗口创建后的外部配置钩子，用于覆盖上面的文本（例如接入调用方的翻译）。</summary>
    public static Action<DefSelectionWindow>? GlobalConfigurator;

    public override Vector2 InitialSize => new(550f, 650f);

    private const float TitleHeight = 30f;
    private const float Spacing = 4f;
    private const float SearchBarHeight = 30f;
    private const float EntryHeight = 28f;
    private const float CategoryHeaderHeight = 24f;
    private const float BottomBarHeight = 35f;
    private const float CopyPasteButtonWidth = 60f;
    private const float ClearButtonWidth = 80f;
    private const float ConfirmCancelButtonWidth = 70f;

    private readonly string title;
    private readonly List<Def> allDefs;
    private Def? selectedDef;
    private readonly Action<Def?>? onConfirmed;

    private Vector2 scrollPosition;

    private struct ListItem
    {
        public enum ItemType { Header, Entry }
        public ItemType type;
        public string label;
        public float yOffset;
        public float height;
        public Def? def;
    }

    private QuickSearchWidget quickSearchWidget = new();
    private List<ListItem> flattenedItems = new();
    private bool needRebuildList = true;

    public DefSelectionWindow(string title, IEnumerable<Def> defs, Def? initiallySelected, Action<Def?>? onConfirmed)
    {
        this.title = title ?? string.Empty;
        this.allDefs = defs?.ToList() ?? new List<Def>();
        this.selectedDef = initiallySelected;
        this.onConfirmed = onConfirmed;
        forcePause = false;
        draggable = true;
        resizeable = true;
        closeOnClickedOutside = true;
        doCloseX = true;
        GlobalConfigurator?.Invoke(this);
    }

    /// <summary>便捷打开方式：加入 WindowStack，选择后通过回调返回结果（取消则不回调）。</summary>
    public static void Open(string title, IEnumerable<Def> defs, Def? initiallySelected, Action<Def?>? onConfirmed)
    {
        Find.WindowStack.Add(new DefSelectionWindow(title, defs, initiallySelected, onConfirmed));
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.UpperLeft;

        float curY = inRect.y;

        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, curY, inRect.width, TitleHeight), title);
        Text.Font = GameFont.Small;
        curY += TitleHeight + Spacing;

        Rect searchRect = new(inRect.x, curY, inRect.width, SearchBarHeight);
        quickSearchWidget.OnGUI(searchRect, Notify_SearchChanged);
        curY += SearchBarHeight + Spacing;

        Rect listRect = new(inRect.x, curY, inRect.width,
            inRect.height - TitleHeight - Spacing - SearchBarHeight - Spacing - BottomBarHeight - Spacing);
        DrawListArea(listRect);
        curY = listRect.yMax + Spacing;

        DrawBottomBar(new Rect(inRect.x, curY, inRect.width, BottomBarHeight));
    }

    private void Notify_SearchChanged()
    {
        scrollPosition.y = 0;
        needRebuildList = true;
    }

    private string GetGroupKey(Def def)
    {
        if (def is StatDef statDef && statDef.category != null)
        {
            return statDef.category.LabelCap.Resolve();
        }
        return def.modContentPack?.Name ?? MiscGroupText;
    }

    private static string GetDefDisplayLabel(Def def)
    {
        if (def is TraitDef traitDef && def.LabelCap.NullOrEmpty())
        {
            var firstDegree = traitDef.degreeDatas.FirstOrDefault();
            if (firstDegree != null)
                return firstDegree.LabelCap;
        }
        return def.LabelCap.Resolve();
    }

    private void RebuildFlattenedItems()
    {
        flattenedItems.Clear();

        var filtered = allDefs.Where(s =>
            quickSearchWidget.filter.Matches(s.defName) ||
            quickSearchWidget.filter.Matches(GetDefDisplayLabel(s))).ToList();

        var grouped = filtered.GroupBy(GetGroupKey)
            .OrderBy(g => g.Key)
            .ToList();

        quickSearchWidget.noResultsMatched = filtered.Count == 0;

        float y = 0;
        foreach (var group in grouped)
        {
            flattenedItems.Add(new ListItem
            {
                type = ListItem.ItemType.Header,
                label = group.Key,
                yOffset = y,
                height = CategoryHeaderHeight
            });
            y += CategoryHeaderHeight;

            foreach (var def in group)
            {
                flattenedItems.Add(new ListItem
                {
                    type = ListItem.ItemType.Entry,
                    label = $"{GetDefDisplayLabel(def)} ({def.defName})",
                    yOffset = y,
                    height = EntryHeight,
                    def = def
                });
                y += EntryHeight + Spacing;
            }
        }

        needRebuildList = false;
    }

    private void DrawListArea(Rect rect)
    {
        if (needRebuildList)
        {
            RebuildFlattenedItems();
        }

        if (flattenedItems.Count == 0) return;

        float totalHeight = flattenedItems[^1].yOffset + flattenedItems[^1].height;
        float viewWidth = rect.width - 16f;
        Rect viewRect = new(0, 0, viewWidth, totalHeight);
        Widgets.BeginScrollView(rect, ref scrollPosition, viewRect);

        int startIndex = FindFirstVisibleItem(scrollPosition.y);
        int endIndex = FindLastVisibleItem(scrollPosition.y + rect.height);

        for (int i = startIndex; i <= endIndex; i++)
        {
            DrawListItem(i, viewWidth);
        }

        Widgets.EndScrollView();
    }

    private int FindFirstVisibleItem(float scrollY)
    {
        for (int i = 0; i < flattenedItems.Count; i++)
        {
            if (flattenedItems[i].yOffset + flattenedItems[i].height > scrollY)
                return i;
        }
        return 0;
    }

    private int FindLastVisibleItem(float viewBottom)
    {
        for (int i = flattenedItems.Count - 1; i >= 0; i--)
        {
            if (flattenedItems[i].yOffset < viewBottom)
                return i;
        }
        return flattenedItems.Count - 1;
    }

    private void DrawListItem(int index, float viewWidth)
    {
        var item = flattenedItems[index];

        if (item.type == ListItem.ItemType.Header)
        {
            Rect headerRect = new(0, item.yOffset, viewWidth, CategoryHeaderHeight);
            Widgets.Label(headerRect, item.label);
        }
        else
        {
            Rect entryRect = new(10f, item.yOffset, viewWidth - 10f, EntryHeight);

            bool isSelected = item.def == selectedDef;
            if (isSelected)
            {
                GUI.color = new Color(0.3f, 0.5f, 0.8f, 0.3f);
                Widgets.DrawBox(entryRect);
                GUI.color = Color.white;
            }

            if (Widgets.ButtonText(entryRect, item.label))
            {
                selectedDef = item.def;
                ConfirmAndClose();
            }

            if (item.def != null && !item.def.description.NullOrEmpty())
            {
                TooltipHandler.TipRegion(entryRect, item.def.description);
            }
        }
    }

    private void DrawBottomBar(Rect rect)
    {
        float curX = rect.x;

        // 中部：当前选择显示（弹性宽度）+ 清除 + 确定 + 取消
        float rightWidth = ClearButtonWidth + ConfirmCancelButtonWidth * 2 + Spacing * 2;
        float labelWidth = Mathf.Max(0f, rect.xMax - curX - rightWidth);

        Rect labelRect = new(curX, rect.y, labelWidth, rect.height);
        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(labelRect, GetCurrentDisplayText());
        Text.Anchor = TextAnchor.UpperLeft;
        curX += labelWidth + Spacing;

        Rect clearBtnRect = new(curX, rect.y, ClearButtonWidth, rect.height);
        if (Widgets.ButtonText(clearBtnRect, ClearButtonText))
        {
            selectedDef = null;
            ConfirmAndClose();
            return;
        }
        curX += ClearButtonWidth + Spacing;

        Rect confirmBtnRect = new(curX, rect.y, ConfirmCancelButtonWidth, rect.height);
        if (Widgets.ButtonText(confirmBtnRect, ConfirmButtonText))
        {
            ConfirmAndClose();
            return;
        }
        curX += ConfirmCancelButtonWidth + Spacing;

        Rect cancelBtnRect = new(curX, rect.y, ConfirmCancelButtonWidth, rect.height);
        if (Widgets.ButtonText(cancelBtnRect, CancelButtonText))
        {
            Close();
        }
    }

    private string GetCurrentDisplayText()
    {
        return selectedDef != null
            ? $"{CurrentText} {GetDefDisplayLabel(selectedDef)} ({selectedDef.defName})"
            : $"{CurrentText} {NoneText}";
    }

    private void ConfirmAndClose()
    {
        onConfirmed?.Invoke(selectedDef);
        Close();
    }
}
