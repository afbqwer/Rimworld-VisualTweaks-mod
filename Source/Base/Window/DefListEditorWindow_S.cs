using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ASQPicker;

/// <summary>
/// 独立单文件 Def 列表编辑窗口：仅依赖游戏本体程序集（UnityEngine / Verse / RimWorld），
/// 搭配 DefSelectionWindow_S.cs 使用。把本文件与 DefSelectionWindow_S.cs 整体复制到其它项目即可使用（可按需修改命名空间）。
/// 用法：DefListEditorWindow.Open("标题", list, typeof(ThingDef), result => { ... });
/// </summary>
public class DefListEditorWindow : Window
{
    // ===== 可本地化文本（公共字段，可直接修改或接入翻译系统） =====
    public string AddButtonText = "添加";
    public string SelectDefText = "选择";
    public string CopyButtonText = "复制";
    public string PasteButtonText = "粘贴";
    public string SaveButtonText = "保存";
    public string CancelButtonText = "取消";

    public override Vector2 InitialSize => new(550f, 650f);

    private const float TitleHeight = 30f;
    private const float Spacing = 4f;
    private const float EntryHeight = 30f;
    private const float DeleteButtonWidth = 30f;
    private const float ButtonHeight = 30f;
    private const float BottomBarHeight = 35f;
    private const float CopyPasteButtonWidth = 60f;

    private readonly string title;
    private readonly Type defType;
    private readonly Action<IList?>? onSave;
    private readonly List<Window> childWindows = new();

    private IList workingList;
    private Vector2 scrollPosition;

    public DefListEditorWindow(string title, IList initialList, Type defType, Action<IList?>? onSave)
    {
        this.title = title ?? string.Empty;
        this.defType = defType;
        this.onSave = onSave;
        workingList = CloneList(initialList);
        forcePause = false;
        draggable = true;
        resizeable = true;
        closeOnClickedOutside = true;
        doCloseX = true;
    }

    /// <summary>
    /// 便捷打开方式：加入 WindowStack，保存时通过回调返回结果（取消则不回调）。
    /// result 始终为 <c>List&lt;T&gt;</c>（T 为 defType），可能为空列表。
    /// </summary>
    public static void Open(string title, IList initialList, Type defType, Action<IList?>? onSave)
    {
        Find.WindowStack.Add(new DefListEditorWindow(title, initialList, defType, onSave));
    }

    /// <summary>打开子窗口（子窗口关闭前，本窗口不会被"点击外部"关掉）。</summary>
    private void OpenChildWindow(Window window)
    {
        childWindows.Add(window);
        Find.WindowStack.Add(window);
    }

    public override void Notify_ClickOutsideWindow()
    {
        childWindows.RemoveAll(w => !Find.WindowStack.IsOpen(w));
        if (childWindows.Count > 0)
            return;
        childWindows.Clear();
        Close();
    }

    private IList CloneList(IList source)
    {
        var clone = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(defType))!;
        foreach (var item in source)
        {
            clone.Add(item);
        }
        return clone;
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

        Rect listRect = new(inRect.x, curY, inRect.width, inRect.height - TitleHeight - Spacing - BottomBarHeight - Spacing);
        DrawListArea(listRect);
        curY = listRect.yMax + Spacing;

        DrawBottomBar(new Rect(inRect.x, curY, inRect.width, BottomBarHeight));
    }

    private void DrawListArea(Rect rect)
    {
        float totalHeight = workingList.Count * (EntryHeight + Spacing) + ButtonHeight + Spacing;

        Rect viewRect = new(0f, 0f, rect.width - 16f, totalHeight);
        Widgets.BeginScrollView(rect, ref scrollPosition, viewRect);

        // 虚拟滚动：只绘制可见范围内的条目（上下各多留一行余量）
        float rowStride = EntryHeight + Spacing;
        int firstVisible = Mathf.Max(0, Mathf.FloorToInt((scrollPosition.y - EntryHeight) / rowStride));
        int lastVisible = Mathf.Min(workingList.Count - 1, Mathf.CeilToInt((scrollPosition.y + rect.height + EntryHeight) / rowStride));

        for (int i = firstVisible; i <= lastVisible; i++)
        {
            DrawEntry(new Rect(0f, i * rowStride, viewRect.width, EntryHeight), i);
        }

        float addButtonY = workingList.Count * rowStride;
        bool addButtonVisible = addButtonY + ButtonHeight >= scrollPosition.y && addButtonY <= scrollPosition.y + rect.height;
        if (addButtonVisible && Widgets.ButtonText(new Rect(0f, addButtonY, viewRect.width, ButtonHeight), AddButtonText))
        {
            var allDefs = GenDefDatabase.GetAllDefsInDatabaseForDef(defType).ToList();
            OpenChildWindow(new DefSelectionWindow(
                title,
                allDefs,
                null,
                selectedDef =>
                {
                    if (selectedDef != null)
                        workingList.Add(selectedDef);
                }));
        }

        Widgets.EndScrollView();
    }

    private void DrawEntry(Rect rect, int index)
    {
        var entry = (Def?)workingList[index];
        float curX = rect.x;

        float availableWidth = rect.width - (DeleteButtonWidth + Spacing);

        Rect defButtonRect = new(curX, rect.y, availableWidth, rect.height);
        string defLabel = entry == null ? SelectDefText : $"{GetDefDisplayLabel(entry)} ({entry.defName})";
        if (Widgets.ButtonText(defButtonRect, defLabel))
        {
            var allDefs = GenDefDatabase.GetAllDefsInDatabaseForDef(defType).ToList();
            OpenChildWindow(new DefSelectionWindow(
                title,
                allDefs,
                entry,
                selectedDef =>
                {
                    workingList[index] = selectedDef;
                }));
        }
        if (entry != null && !entry.description.NullOrEmpty())
        {
            TooltipHandler.TipRegion(defButtonRect, entry.description);
        }
        curX += availableWidth + Spacing;

        Rect deleteRect = new(curX, rect.y, DeleteButtonWidth, rect.height);
        if (Widgets.ButtonText(deleteRect, "X"))
        {
            workingList.RemoveAt(index);
        }
    }

    private void DrawBottomBar(Rect rect)
    {
        float curX = rect.x;

        if (Widgets.ButtonText(new Rect(curX, rect.y, CopyPasteButtonWidth, rect.height), CopyButtonText))
        {
            CopyToClipboard();
        }
        curX += CopyPasteButtonWidth + Spacing;

        if (Widgets.ButtonText(new Rect(curX, rect.y, CopyPasteButtonWidth, rect.height), PasteButtonText))
        {
            PasteFromClipboard();
        }
        curX += CopyPasteButtonWidth + Spacing;

        float remainingWidth = rect.xMax - curX;
        float btnWidth = (remainingWidth - Spacing) / 2f;

        if (Widgets.ButtonText(new Rect(curX, rect.y, btnWidth, rect.height), SaveButtonText))
        {
            SaveAndClose();
            return;
        }
        curX += btnWidth + Spacing;

        if (Widgets.ButtonText(new Rect(curX, rect.y, btnWidth, rect.height), CancelButtonText))
        {
            Close();
        }
    }

    private void CopyToClipboard()
    {
        var defNames = new List<string>();
        foreach (var item in workingList)
            if (item is Def def)
                defNames.Add(def.defName);

        if (defNames.Count > 0)
            GUIUtility.systemCopyBuffer = string.Join("\n", defNames);
    }

    private void PasteFromClipboard()
    {
        string clipboardData = GUIUtility.systemCopyBuffer;
        if (clipboardData.NullOrEmpty())
            return;

        var defNames = clipboardData.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var newList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(defType))!;
        foreach (var defName in defNames)
        {
            var def = GenDefDatabase.GetDefSilentFail(defType, defName.Trim());
            if (def != null)
                newList.Add(def);
        }
        if (newList.Count > 0)
        {
            workingList = newList;
            scrollPosition.y = 0f;
        }
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

    private void SaveAndClose()
    {
        var finalList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(defType))!;
        foreach (var item in workingList)
        {
            if (item != null)
                finalList.Add(item);
        }
        onSave?.Invoke(finalList);
        Close();
    }
}
