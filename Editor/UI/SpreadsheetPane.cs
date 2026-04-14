#define REMOVE_VIEW_HISTORY
using UnityEngine;
using UnityEditor;
using Unity.MemoryProfiler.Editor.Database;

namespace Unity.MemoryProfiler.Editor.UI
{
    internal class SpreadsheetPane : ViewPane
    {
        public override string ViewName { get { return TableDisplayName; } }

        public string TableDisplayName
        {
            get
            {
                return m_Spreadsheet.SourceTable.GetDisplayName();
            }
        }

        UI.DatabaseSpreadsheet m_Spreadsheet;
        Database.TableReference m_CurrentTableLink;

        public int CurrentTableIndex { get; private set; }

        protected bool m_NeedRefresh = false;
#if !REMOVE_VIEW_HISTORY
        public override bool ViewStateFilteringChangedSinceLastSelectionOrViewClose => m_ViewStateFilteringChangedSinceLastSelectionOrViewClose;
        bool m_ViewStateFilteringChangedSinceLastSelectionOrViewClose = false;


        internal class ViewStateHistory : ViewStateChangedHistoryEvent
        {
            public readonly DatabaseSpreadsheet.State SpreadsheetState;

            public ViewStateHistory(DatabaseSpreadsheet.State spreadsheetState)
            {
                SpreadsheetState = spreadsheetState;
            }

            protected override bool IsEqual(HistoryEvent evt)
            {
                var other = evt as ViewStateHistory;
                return other != null &&
                    SpreadsheetState.Equals(other.SpreadsheetState);
            }
        }

        internal class History : ViewOpenHistoryEvent
        {
            public override ViewStateChangedHistoryEvent ViewStateChangeRestorePoint => m_ViewStateHistory;

            ViewStateHistory m_ViewStateHistory;
            readonly Database.TableReference m_Table;

            public History(SpreadsheetPane spreadsheetPane, UIState.BaseMode mode, Database.CellLink cell)
            {
                Mode = mode;
                m_Table = spreadsheetPane.m_CurrentTableLink;
                m_ViewStateHistory = new ViewStateHistory(spreadsheetPane.m_Spreadsheet.CurrentState);
            }

            public void Restore(SpreadsheetPane pane, bool reopen = false, ViewStateChangedHistoryEvent viewState = null, SelectionEvent selectionEvent = null, bool selectionIsLatent = false)
            {
                ViewStateHistory viewStateToRestore = m_ViewStateHistory;
                if (viewState != null && viewState is ViewStateHistory)
                    viewStateToRestore = viewState as ViewStateHistory;

                if (reopen)
                {
                    var table = pane.m_UIState.CurrentMode.GetSchema().GetTableByReference(m_Table);
                    if (table == null)
                    {
                        Debug.LogError("No table named '" + m_Table.Name + "' found.");
                        return;
                    }
                    pane.m_CurrentTableLink = m_Table;
                    pane.CurrentTableIndex = pane.m_UIState.CurrentMode.GetTableIndex(table);
                    pane.m_Spreadsheet = new UI.DatabaseSpreadsheet(pane.m_UIState.FormattingOptions, table, pane, viewStateToRestore.SpreadsheetState);
                    pane.m_Spreadsheet.UserChangedFilters += pane.OnUserChangedSpreadsheetFilters;
                    pane.m_Spreadsheet.LinkClicked += pane.OnSpreadsheetClick;
                    pane.m_Spreadsheet.RowSelectionChanged += pane.OnRowSelected;
                    pane.m_EventListener.OnRepaint();
                }

                pane.m_Spreadsheet.CurrentState = viewStateToRestore.SpreadsheetState;

                // restore the selection, needs to happen after first state reset to ensure selected item is found correctly
                if (selectionEvent != null)
                {
                    var state = viewStateToRestore.SpreadsheetState;
                    state.SelectedRow = selectionEvent.Selection.FindSelectionInTable(pane.m_UIState, pane.m_Spreadsheet.DisplayTable);
                    state.SelectionIsLatent = selectionIsLatent;
                    viewStateToRestore = new ViewStateHistory(state);

                    pane.m_Spreadsheet.CurrentState = viewStateToRestore.SpreadsheetState;
                }
            }

            public override string ToString()
            {
                string s = Mode.GetSchema().GetDisplayName() + seperator + m_Table.Name;
                if (m_Table.Param != null)
                {
                    s += "(";
                    string sp = "";
                    foreach (var p in m_Table.Param.AllParameters)
                    {
                        if (sp != "")
                        {
                            sp += ", ";
                        }
                        sp += p.Key;
                        sp += "=";
                        sp += p.Value.GetValueString(0, Database.DefaultDataFormatter.Instance);
                    }
                    s += sp + ")";
                }
                return s;
            }

            protected override bool IsEqual(HistoryEvent evt)
            {
                var hEvt = evt as History;
                if (hEvt == null)
                    return false;

                return m_Table == hEvt.m_Table
                    && m_ViewStateHistory.SpreadsheetState.Filter == hEvt.m_ViewStateHistory.SpreadsheetState.Filter
                    && m_ViewStateHistory.SpreadsheetState.FirstVisibleRow == hEvt.m_ViewStateHistory.SpreadsheetState.FirstVisibleRow
                    && m_ViewStateHistory.SpreadsheetState.FirstVisibleRowIndex == hEvt.m_ViewStateHistory.SpreadsheetState.FirstVisibleRowIndex
                    && m_ViewStateHistory.SpreadsheetState.SelectedCell == hEvt.m_ViewStateHistory.SpreadsheetState.SelectedCell
                    && m_ViewStateHistory.SpreadsheetState.SelectedRow == hEvt.m_ViewStateHistory.SpreadsheetState.SelectedRow;
            }
        }
#endif

        public SpreadsheetPane(IUIStateHolder s, IViewPaneEventListener l)
            : base(s, l)
        {
        }

        protected void CloseCurrentTable()
        {
            if (m_Spreadsheet != null)
            {
                if (m_Spreadsheet.SourceTable is Database.ExpandTable)
                {
                    (m_Spreadsheet.SourceTable as Database.ExpandTable).ResetAllGroup();
                }
            }
        }

        public void OpenLinkRequest(Database.LinkRequestTable link)
        {
            var tableRef = new Database.TableReference(link.LinkToOpen.TableName, link.Parameters);
            var table = m_UIState.CurrentMode.GetSchema().GetTableByReference(tableRef);
            if (table == null)
            {
                UnityEngine.Debug.LogError("No table named '" + link.LinkToOpen.TableName + "' found.");
                return;
            }
            OpenLinkRequest(link, tableRef, table);
        }

        public bool OpenLinkRequest(Database.LinkRequestTable link, Database.TableReference tableLink, Database.Table table)
        {
            if (link.LinkToOpen.RowWhere != null && link.LinkToOpen.RowWhere.Count > 0)
            {
                Database.Table filteredTable = table;
                if (table.GetMetaData().defaultFilter != null)
                {
                    filteredTable = table.GetMetaData().defaultFilter.CreateFilter(table);
                }
                var whereUnion = new Database.View.WhereUnion(link.LinkToOpen.RowWhere, null, null, null, null, m_UIState.CurrentMode.GetSchema(), filteredTable, link.SourceView == null ? null : link.SourceView.ExpressionParsingContext);
                long rowToSelect = whereUnion.GetIndexFirstMatch(link.SourceRow);
                if (rowToSelect < 0)
                {
                    Debug.LogWarning("Could not find entry in target table '" + link.LinkToOpen.TableName + "'");
                    return false;
                }

                OpenTable(tableLink, table, new Database.CellPosition(rowToSelect, 0));
            }
            else
            {
                OpenTable(tableLink, table, new Database.CellPosition(0, 0));
            }
            return true;
        }

        void OnSpreadsheetClick(UI.DatabaseSpreadsheet sheet, Database.LinkRequest link, Database.CellPosition pos)
        {
            if (link.IsPingLink)
            {
                (link as Database.LinkRequestSceneHierarchy).Ping();
                return;
            }

#if !REMOVE_VIEW_HISTORY
            var hEvent = new History(this, m_UIState.CurrentMode, sheet.DisplayTable.GetLinkTo(pos));
            m_UIState.history.AddEvent(hEvent);
#endif
            m_EventListener.OnOpenLink(link);
        }

        void OnRowSelected(long rowIndex)
        {
            var selection = new MemorySampleSelection(m_UIState, m_Spreadsheet.DisplayTable, rowIndex);
            m_UIState.RegisterSelectionChangeEvent(selection);
        }

        void OnUserChangedSpreadsheetFilters()
        {
            var selection = m_UIState.history.GetLastSelectionEvent(MemorySampleSelectionRank.MainSelection);
            if (selection != null && selection.Selection.Valid)
            {
                ApplyActiveSelectionAfterOpening(selection);
            }
        }

        public override void SetSelectionFromHistoryEvent(SelectionEvent selectionEvent)
        {
            if (selectionEvent.Selection.Rank == MemorySampleSelectionRank.MainSelection)
                m_Spreadsheet.RestoreSelectedRow(selectionEvent.Selection.FindSelectionInTable(m_UIState, m_Spreadsheet.DisplayTable));
            else
            {
                var currentState = m_Spreadsheet.CurrentState;
                currentState.SelectionIsLatent = true;
                m_Spreadsheet.CurrentState = currentState;
                m_EventListener.OnRepaint();
            }
        }

        public void OpenTable(Database.TableReference tableRef, Database.Table table)
        {
            CloseCurrentTable();
            m_CurrentTableLink = tableRef;
            CurrentTableIndex = m_UIState.CurrentMode.GetTableIndex(table);
            m_Spreadsheet = new UI.DatabaseSpreadsheet(m_UIState.FormattingOptions, table, this);
            m_Spreadsheet.UserChangedFilters += OnUserChangedSpreadsheetFilters;
            m_Spreadsheet.LinkClicked += OnSpreadsheetClick;
            m_Spreadsheet.RowSelectionChanged += OnRowSelected;
            m_EventListener.OnRepaint();
        }

        public void OpenTable(Database.TableReference tableRef, Database.Table table, Database.CellPosition pos)
        {
            CloseCurrentTable();
            m_CurrentTableLink = tableRef;
            CurrentTableIndex = m_UIState.CurrentMode.GetTableIndex(table);
            m_Spreadsheet = new UI.DatabaseSpreadsheet(m_UIState.FormattingOptions, table, this);
            m_Spreadsheet.UserChangedFilters += OnUserChangedSpreadsheetFilters;
            m_Spreadsheet.LinkClicked += OnSpreadsheetClick;
            m_Spreadsheet.RowSelectionChanged += OnRowSelected;
            m_Spreadsheet.Goto(pos);
            m_EventListener.OnRepaint();
        }

#if !REMOVE_VIEW_HISTORY
        public void OpenHistoryEvent(History e, bool reopen, ViewStateChangedHistoryEvent viewStateToRestore = null, SelectionEvent selectionEvent = null, bool selectionIsLatent = false)
        {
            if (e == null) return;
            e.Restore(this, reopen, viewStateToRestore, selectionEvent, selectionIsLatent);
        }

#endif

#if !REMOVE_VIEW_HISTORY
        public override UI.ViewOpenHistoryEvent GetOpenHistoryEvent()
        {
            if (m_Spreadsheet != null && m_CurrentTableLink != null)
            {
                var c = m_Spreadsheet.GetLinkToCurrentSelection();
                if (c == null)
                {
                    c = m_Spreadsheet.GetLinkToFirstVisible();
                }
                if (c != null)
                {
                    var hEvent = new History(this, m_UIState.CurrentMode, c);
                    return hEvent;
                }
            }
            return null;
        }

        public override ViewStateChangedHistoryEvent GetViewStateFilteringChangesSinceLastSelectionOrViewClose()
        {
            m_ViewStateFilteringChangedSinceLastSelectionOrViewClose = false;

            var stateEvent = new ViewStateHistory(m_Spreadsheet.CurrentState);
            stateEvent.ChangeType = ViewStateChangedHistoryEvent.StateChangeType.FiltersChanged;
            return stateEvent;
        }

#endif
        private void OnGUI_OptionBar()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var ff = GUILayout.Toggle(m_UIState.FormattingOptions.ObjectDataFormatter.flattenFields, "Flatten Fields");
            if (m_UIState.FormattingOptions.ObjectDataFormatter.flattenFields != ff)
            {
                m_UIState.FormattingOptions.ObjectDataFormatter.flattenFields = ff;
                if (m_Spreadsheet != null)
                {
                    m_NeedRefresh = true;
                }
            }
            var fsf = GUILayout.Toggle(m_UIState.FormattingOptions.ObjectDataFormatter.flattenStaticFields, "Flatten Static Fields");
            if (m_UIState.FormattingOptions.ObjectDataFormatter.flattenStaticFields != fsf)
            {
                m_UIState.FormattingOptions.ObjectDataFormatter.flattenStaticFields = fsf;
                if (m_Spreadsheet != null)
                {
                    m_NeedRefresh = true;
                }
            }
            
            // Export to CSV button
            if (GUILayout.Button("Export to CSV", GUILayout.Width(100)))
            {
                // 延迟调用，避免在 GUI 布局事件中打开模态对话框导致布局栈错误
                EditorApplication.delayCall += ExportTableToCSV;
            }
            
            EditorGUILayout.EndHorizontal();
        }

        public override void OnGUI(Rect r)
        {
            if (Event.current.type == EventType.Layout)
            {
                if (m_NeedRefresh)
                {
                    m_Spreadsheet.UpdateTable();
                    m_NeedRefresh = false;
                }
            }
            m_UIState.FormattingOptions.ObjectDataFormatter.forceLinkAllObject = false;
            if (m_Spreadsheet != null)
            {
                GUILayout.BeginArea(r);
                EditorGUILayout.BeginVertical();
                EditorGUILayout.BeginHorizontal();
                m_Spreadsheet.OnGui_Filters();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(2);
                m_Spreadsheet.OnGUI(r.width);
                GUILayout.Space(2);
                EditorGUILayout.EndHorizontal();

                OnGUI_OptionBar();
                GUILayout.Space(2);
                EditorGUILayout.EndVertical();
                GUILayout.EndArea();
                if (m_NeedRefresh)
                {
                    m_EventListener.OnRepaint();
                }
            }
        }

        public override void OnSelectionChanged(MemorySampleSelection selection)
        {
            if (m_Spreadsheet == null)
                return; // Domain Reload or Serialization/Deserialization related untimely event fired. Ignore it, this view is closed for business.

            if (selection.Rank == MemorySampleSelectionRank.SecondarySelection)
                m_Spreadsheet.SetSelectionAsLatent(true);
            switch (selection.Type)
            {
                case MemorySampleSelectionType.NativeObject:
                case MemorySampleSelectionType.ManagedObject:
                case MemorySampleSelectionType.UnifiedObject:
                case MemorySampleSelectionType.NativeType:
                case MemorySampleSelectionType.ManagedType:
                case MemorySampleSelectionType.Allocation:
                case MemorySampleSelectionType.AllocationSite:
                case MemorySampleSelectionType.Symbol:
                case MemorySampleSelectionType.AllocationCallstack:
                case MemorySampleSelectionType.NativeRegion:
                case MemorySampleSelectionType.ManagedRegion:
                case MemorySampleSelectionType.Allocator:
                case MemorySampleSelectionType.Label:
                case MemorySampleSelectionType.Connection:
                    // TODO: check that this is the type of item currently shown and if the selection wasn't made in this spreadsheet, that it is appropriately updated. For now, assume it was made in this spreadsheet.
                    break;
                case MemorySampleSelectionType.None:
                case MemorySampleSelectionType.HighlevelBreakdownElement:
                default:
                    if (selection.Rank == MemorySampleSelectionRank.MainSelection)
                        m_Spreadsheet.ClearSelection();
                    break;
            }
        }

        /// <summary>
        /// 从表链中查找 ObjectListTable（可能被 Filter/Sort/View 包装）
        /// </summary>
        ObjectListTable FindObjectListTable(Database.Table table)
        {
            if (table is ObjectListTable olt)
                return olt;

            // IndexedTable 是 SortedTable 和 MatchTable 的基类
            if (table is Database.Operation.IndexedTable it)
                return FindObjectListTable(it.m_SourceTable);

            // 反射后备 - 查找字段
            var sourceTableField = table.GetType().GetField("m_SourceTable");
            if (sourceTableField != null)
            {
                var sourceTable = sourceTableField.GetValue(table) as Database.Table;
                if (sourceTable != null && sourceTable != table)
                    return FindObjectListTable(sourceTable);
            }

            // 反射后备 - 查找属性
            var sourceTableProp = table.GetType().GetProperty("SourceTable");
            if (sourceTableProp != null)
            {
                var sourceTable = sourceTableProp.GetValue(table) as Database.Table;
                if (sourceTable != null && sourceTable != table)
                    return FindObjectListTable(sourceTable);
            }

            return null;
        }

        /// <summary>
        /// 获取引用某对象的所有对象的详细信息字符串
        /// </summary>
        string GetReferencedByDetails(CachedSnapshot snapshot, ObjectData objData)
        {
            try
            {
                var referencingObjects = objData.GetAllReferencingObjects(snapshot);
                if (referencingObjects == null || referencingObjects.Length == 0)
                    return "";

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < referencingObjects.Length; i++)
                {
                    if (i > 0) sb.Append("; ");
                    var refObj = referencingObjects[i].displayObject;
                    string typeName = "";
                    string objName = "";

                    switch (refObj.dataType)
                    {
                        case ObjectDataType.NativeObject:
                            int iType = snapshot.NativeObjects.NativeTypeArrayIndex[refObj.nativeObjectIndex];
                            typeName = snapshot.NativeTypes.TypeName[iType];
                            objName = snapshot.NativeObjects.ObjectName[refObj.nativeObjectIndex];
                            break;
                        case ObjectDataType.Array:
                        case ObjectDataType.BoxedValue:
                        case ObjectDataType.Object:
                        case ObjectDataType.Value:
                        case ObjectDataType.ReferenceArray:
                        case ObjectDataType.ReferenceObject:
                            if (refObj.managedTypeIndex >= 0 && refObj.managedTypeIndex < snapshot.TypeDescriptions.TypeDescriptionName.Length)
                                typeName = snapshot.TypeDescriptions.TypeDescriptionName[refObj.managedTypeIndex];
                            else
                                typeName = "<unknown type>";
                            // 尝试获取 managed 对象的名称（如果关联了 native 对象）
                            if (refObj.IsValid)
                            {
                                var ptr = refObj.hostManagedObjectPtr;
                                if (ptr > 0 && snapshot.CrawledData != null && snapshot.CrawledData.MangedObjectIndexByAddress != null)
                                {
                                    int moiIdx;
                                    if (snapshot.CrawledData.MangedObjectIndexByAddress.TryGetValue(ptr, out moiIdx))
                                    {
                                        var moi = snapshot.CrawledData.ManagedObjects[moiIdx];
                                        if (moi.IsValid() && moi.NativeObjectIndex >= 0)
                                            objName = snapshot.NativeObjects.ObjectName[moi.NativeObjectIndex];
                                    }
                                }
                            }
                            break;
                        default:
                            typeName = refObj.dataType.ToString();
                            break;
                    }

                    if (!string.IsNullOrEmpty(objName))
                        sb.Append($"[{typeName}] {objName}");
                    else
                        sb.Append($"[{typeName}]");
                }
                return sb.ToString();
            }
            catch (System.Exception)
            {
                return "";
            }
        }

        void ExportTableToCSV()
        {
            if (m_Spreadsheet == null || m_Spreadsheet.DisplayTable == null)
            {
                EditorUtility.DisplayDialog("Export Error", "No data to export.", "OK");
                return;
            }

            var table = m_Spreadsheet.DisplayTable;
            var tableName = TableDisplayName.Replace("/", "_").Replace("\\", "_");
            
            var path = EditorUtility.SaveFilePanel(
                "Export Table to CSV",
                "",
                $"{tableName}.csv",
                "csv");

            if (string.IsNullOrEmpty(path))
                return;

            // 尝试从 SourceTable 找到 ObjectListTable 以获取引用详情
            ObjectListTable objectListTable = FindObjectListTable(m_Spreadsheet.SourceTable);
            CachedSnapshot snapshot = objectListTable?.Snapshot;

            // 查找 DisplayTable 中的 "Index" 列索引（unified object index），用于获取 ObjectData
            int indexColumnIdx = -1;
            var meta = table.GetMetaData();
            var columnCount = meta.GetColumnCount();
            if (snapshot != null)
            {
                for (int col = 0; col < columnCount; col++)
                {
                    var metaCol = meta.GetColumnByIndex(col);
                    if (metaCol.Name == "Index")
                    {
                        indexColumnIdx = col;
                        break;
                    }
                }
            }
            bool hasRefDetails = (snapshot != null && indexColumnIdx >= 0);

            try
            {
                using (var writer = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    // 写入标题行
                    for (int col = 0; col < columnCount; col++)
                    {
                        if (col > 0) writer.Write(",");
                        var column = meta.GetColumnByIndex(col);
                        var columnName = m_UIState.FormattingOptions.ObjectDataFormatter.ShowPrettyNames ? column.DisplayName : column.Name;
                        writer.Write(EscapeCSVField(columnName));
                    }
                    // 追加 Referenced By Details 列标题
                    if (hasRefDetails)
                    {
                        writer.Write(",");
                        writer.Write(EscapeCSVField("Referenced By Details"));
                    }
                    writer.WriteLine();

                    // 写入数据行
                    var rowCount = table.GetRowCount();
                    var indexColumn = indexColumnIdx >= 0 ? table.GetColumnByIndex(indexColumnIdx) : null;

                    for (long row = 0; row < rowCount; row++)
                    {
                        for (int col = 0; col < columnCount; col++)
                        {
                            if (col > 0) writer.Write(",");
                            
                            var column = table.GetColumnByIndex(col);
                            var metaColumn = meta.GetColumnByIndex(col);
                            var cellString = "";
                            if (column != null && rowCount == column.GetRowCount())
                            {
                                cellString = column.GetRowValueString(row, m_UIState.FormattingOptions.GetFormatter(metaColumn.FormatName));
                            }
                            writer.Write(EscapeCSVField(cellString));
                        }

                        // 追加 Referenced By Details 列数据
                        if (hasRefDetails)
                        {
                            writer.Write(",");
                            string refDetails = "";
                            try
                            {
                                var indexStr = indexColumn.GetRowValueString(row, DefaultDataFormatter.Instance);
                                long unifiedIndex;
                                if (long.TryParse(indexStr, out unifiedIndex) && unifiedIndex >= 0)
                                {
                                    var objData = ObjectData.FromUnifiedObjectIndex(snapshot, unifiedIndex);
                                    if (objData.IsValid)
                                    {
                                        refDetails = GetReferencedByDetails(snapshot, objData);
                                    }
                                }
                            }
                            catch (System.Exception)
                            {
                                refDetails = "";
                            }
                            writer.Write(EscapeCSVField(refDetails));
                        }

                        writer.WriteLine();
                    }
                }

                EditorUtility.DisplayDialog("Export Successful", $"Data exported to:\n{path}", "OK");
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Export Error", $"Failed to export data:\n{ex.Message}", "OK");
                Debug.LogError($"Failed to export table to CSV: {ex}");
            }
        }

        string EscapeCSVField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return "";

            // 如果字段包含逗号、引号或换行符,需要用引号包围并转义内部引号
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
            {
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            }

            return field;
        }

        public override void OnClose()
        {
            MemoryProfilerAnalytics.SendPendingFilterChanges();
            CloseCurrentTable();
            m_Spreadsheet = null;
        }
    }
}
