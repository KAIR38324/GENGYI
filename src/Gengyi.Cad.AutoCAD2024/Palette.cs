using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.AutoCAD.Windows;
using BlockTable=Autodesk.AutoCAD.DatabaseServices.BlockTable;
using OpenMode=Autodesk.AutoCAD.DatabaseServices.OpenMode;
using Gengyi.Core;
using CadApplication=Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CheckState=Gengyi.Core.CheckState;

namespace Gengyi.Cad.AutoCAD2024
{
    internal sealed class SearchDialog : Form
    {
        private readonly CancellationTokenSource cancellation=new CancellationTokenSource();
        private readonly Task<SearchResult> task;
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=60};
        internal bool Cancelled;
        internal SearchDialog(Project project,Room room)
        {
            Text="生成更衣布置候选";Size=new Size(390,150);FormBorderStyle=FormBorderStyle.FixedDialog;
            StartPosition=FormStartPosition.CenterParent;MaximizeBox=false;MinimizeBox=false;
            Controls.Add(new Label{Text="计算当前房间的不同方案与几何检查…",AutoSize=true,Location=new Point(18,20)});
            var cancel=new Button{Text="取消",Location=new Point(270,65)};
            cancel.Click+=(_,__)=>{Cancelled=true;cancellation.Cancel();cancel.Enabled=false;cancel.Text="正在取消";};Controls.Add(cancel);
            task=Task.Run(()=>Gengyi.Core.Layout.Generate(project,room,cancellation.Token),cancellation.Token);
            timer.Tick+=(_,__)=>{if(task.IsCompleted){timer.Stop();DialogResult=Cancelled?DialogResult.Cancel:DialogResult.OK;Close();}};
            FormClosing+=(_,e)=>{if(!task.IsCompleted){e.Cancel=true;Cancelled=true;cancellation.Cancel();}};
            timer.Start();
        }
        internal SearchResult Result()=>task.GetAwaiter().GetResult();
        protected override void Dispose(bool disposing)
        {if(disposing){timer.Dispose();cancellation.Dispose();}base.Dispose(disposing);}
    }

    internal sealed class Palette : IDisposable
    {
        private static readonly Color Ink=Color.FromArgb(35,49,70),Muted=Color.FromArgb(100,113,132),Blue=Color.FromArgb(42,101,207),Background=Color.FromArgb(240,243,248);
        private readonly PaletteSet palette;
        private readonly UserControl surface=new UserControl{Dock=DockStyle.Fill,BackColor=Background};
        private readonly TabControl tabs=new TabControl{Dock=DockStyle.Fill,Padding=new Point(12,7)};
        private readonly ComboBox rooms=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,Height=30};
        private readonly ComboBox shiftCount=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};
        private readonly NumericUpDown shift=Number(10000,300),male=Number(100,50);
        private readonly NumericUpDown aisle=Number(10000,1000,100),margin=Number(10000,1000),operation=Number(10000,0),rear=Number(1000,0),maxSegment=Number(50000,3000);
        private readonly NumericUpDown pitch=Number(10000,100),startMargin=Number(10000,50),endMargin=Number(10000,50),elevation=Number(20000,0);
        private readonly NumericUpDown walkWidth=Number(2000,600),segmentGap=Number(10000,100),customPeople=Number(10000,0);
        private readonly CheckBox orth=Check("沿房间方向正交拖动、旋转");
        private readonly RadioButton noStack=new RadioButton{Text="不叠放 · 全部使用单柜",AutoSize=true,ForeColor=Ink,Margin=new Padding(0,4,0,6)};
        private readonly RadioButton stack=new RadioButton{Text="允许叠放 · 按24—48—24交错",AutoSize=true,ForeColor=Ink,Margin=new Padding(0,4,0,6)};
        private readonly CheckBox actionsVerified=Check("已实际核对柜门、更衣动作和操作深度"),installationVerified=Check("已实际核对吊挂标高、支撑及衣物占用");
        private readonly CheckedListBox shoeTypes=new CheckedListBox{Height=68,CheckOnClick=true,BorderStyle=BorderStyle.FixedSingle};
        private readonly ComboBox rectangleChoices=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,IntegralHeight=false,DropDownHeight=160};
        private readonly PlanningSketch sketch=new PlanningSketch();
        private readonly PlanningSketch boundarySketch=new PlanningSketch{Height=116};
        private readonly ListBox candidates=new ListBox{Height=154,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=50,BorderStyle=BorderStyle.FixedSingle,IntegralHeight=false};
        private readonly TextBox statistics=new TextBox{Height=260,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.FixedSingle,BackColor=Color.White};
        private readonly Label areaSummary=Text("正在计算尺寸建议…",true),areaDetail=Text(""),assumptions=Text(""),boundaryInfo=Text("尚未确定房间范围"),sourceInfo=Text(""),candidateDetail=Text(""),footerTitle=Text("先选房间，输入人数",true),footerDetail=Text("面积建议会在定框前显示");
        private readonly Label pendingInfo=Text("修改人数会立即更新面积建议；操作前自动保存参数。"),stackingStatus=Text(""),planningPreviewStatus=Text(""),workflowHint=Text(""),inputError=Text(""),legacyPeople=Text(""),accessInfo=Text("");
        private readonly Button nextStep,planPreview,suggested,manualRectangle,existingRectangle,generate,commit,check,preview,cancel,obstacles,doors,passages,closures,rebuild,bind;
        private readonly TableLayoutPanel stackingSettings,shoeSettings,hangerSettings;
        private readonly TableLayoutPanel sizingSection,boundarySection,schemeSection;
        private readonly TabPage workflowPage;
        private readonly TableLayoutPanel customRow;
        private readonly ToolTip tips=new ToolTip{AutoPopDelay=20000,InitialDelay=300,ReshowDelay=100};
        private readonly System.Windows.Forms.Timer estimateTimer=new System.Windows.Forms.Timer{Interval=160};
        private readonly Font boldFont;
        private bool updating,inputsDirty,planningPreviewCurrent,candidateCanCommit,refreshingEstimate,preferStackedOption;
        private int restoredPlanningOption=-1;
        private string boundRoomId="",roomList="",candidateSignature="",candidateIds="";
        private Autodesk.AutoCAD.ApplicationServices.Document? boundDocument;
        private Room? planningRoom;
        private AreaEstimate? estimate;
        private Session? lastSession;
        private Preview? observedPlanningPreview;
        private long observedPlanningRevision;
        private static NumericUpDown Number(int max,int value,int min=0)=>new NumericUpDown{Maximum=max,Minimum=min,Value=value,Dock=DockStyle.Fill,Height=28,ThousandsSeparator=true};
        private static CheckBox Check(string text)=>new CheckBox{Text=text,AutoSize=true,ForeColor=Ink,Margin=new Padding(0,4,0,6)};
        private static Label Text(string text,bool strong=false)=>new Label{Text=text,AutoSize=true,ForeColor=strong?Ink:Muted,Margin=new Padding(0,0,0,6),Dock=DockStyle.Fill};

        internal Palette()
        {
            surface.Font=new Font("Microsoft YaHei UI",9F);boldFont=new Font(surface.Font,FontStyle.Bold);
            palette=new PaletteSet("GENGYI · 更衣设施布置 v"+typeof(Palette).Assembly.GetName().Version?.ToString(3),new Guid("729EFC43-3F88-41D0-A63C-EA2D873EE988"))
            {MinimumSize=new Size(420,570),Size=new Size(470,920),Style=PaletteSetStyles.ShowCloseButton|PaletteSetStyles.ShowAutoHideButton|PaletteSetStyles.ShowPropertiesMenu};
            var header=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Padding=new Padding(14,12,14,8),BackColor=Color.White};
            var brand=Text("GENGYI  /  更衣设施布置",true);brand.Font=boldFont;Add(header,brand);Add(header,rooms);
            tips.SetToolTip(rooms,"每个房间分别保存自己的范围、方案和设备。切换前自动保存已改的人数参数。");
            var footer=new TableLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,ColumnCount=1,Padding=new Padding(14,9,14,7),BackColor=Color.FromArgb(229,236,249)};
            footerTitle.Font=boldFont;Add(footer,footerTitle);Add(footer,footerDetail);
            nextStep=ActionButton("下一步 · 预览设备与参考尺寸",NextStep,true);Add(footer,nextStep);
            surface.Controls.Add(tabs);surface.Controls.Add(footer);surface.Controls.Add(header);palette.Add("布置工作台",surface);
            var work=Page("布置");var edit=Page("设备与图块");var settings=Page("高级参数");
            workflowPage=(TabPage)work.Parent;

            var people=Card(work,"1  班次与人数","换鞋间和一更按所选班次合计，二更按一班周转。各房间按基础人数 × 1.1 向上取整。",boldFont);
            shiftCount.Items.AddRange(new object[]{"单班","双班"});shiftCount.SelectedIndex=1;
            Add(people,Text("班次"));Add(people,shiftCount);
            NumberRow(people,"每班人数",shift,"男性比例 %",male);
            customRow=NumberRow(people,"当前自定义房每班人数",customPeople);
            Add(people,legacyPeople);Add(people,pendingInfo);inputError.ForeColor=Color.Firebrick;Add(people,inputError);
            stackingSettings=Card(work,"2  是否允许叠放","先确定柜体形式，再预览设备和参考占地。",boldFont);
            Add(stackingSettings,noStack);Add(stackingSettings,stack);
            Add(stackingSettings,Text("48人双柜的两个相邻柜必须都是24人单柜；双柜自身不作为跨坐位置。"));Add(stackingSettings,stackingStatus);
            var area=Card(work,"3  预览面积、设备与尺寸","先看参考方案；预览不会替你选定实际房间边界。",boldFont);
            sizingSection=area;
            areaSummary.Font=boldFont;Add(area,areaSummary);Add(area,rectangleChoices);Add(area,sketch);Add(area,areaDetail);
            var assumptionToggle=new LinkLabel{Text="查看估算条件",AutoSize=true,LinkColor=Blue,Margin=new Padding(0,0,0,6)};
            assumptionToggle.LinkClicked+=(_,__)=>{assumptions.Visible=!assumptions.Visible;assumptionToggle.Text=assumptions.Visible?"收起估算条件":"查看估算条件";};
            Add(area,assumptionToggle);assumptions.Visible=false;Add(area,assumptions);
            planPreview=ActionButton("在 CAD 中预览设备与尺寸",StartPlanningPreview,true);Add(area,planPreview);Add(area,planningPreviewStatus);
            var boundaryCard=Card(work,"4  选择自己的房间边界","支持闭合的不规则轮廓或矩形；实际范围由你选择。",boldFont);
            boundarySection=boundaryCard;
            existingRectangle=ActionButton("选择我的房间边界（不规则 / 矩形）",()=>SelectBoundary("GYRECT Select"),true);Add(boundaryCard,existingRectangle);
            manualRectangle=ActionButton("手动点两个角选矩形",()=>SelectBoundary("GYRECT Corners"));
            suggested=ActionButton("使用参考矩形",StartSuggested);ButtonRow(boundaryCard,manualRectangle,suggested);
            tips.SetToolTip(existingRectangle,"先完成当前人数和叠放设置的预览，再在 CAD 中点选自己的闭合边界。凹形、不规则轮廓按真实面积与形状计算。");
            boundaryInfo.BackColor=Color.FromArgb(245,247,251);boundaryInfo.Padding=new Padding(8);Add(boundaryCard,boundaryInfo);Add(boundaryCard,boundarySketch);
            doors=CommandButton("设置出入口", "GYDOORS",true);passages=CommandButton("选通行保留区", "GYPASSAGES");
            ButtonRow(boundaryCard,doors,passages);Add(boundaryCard,accessInfo);
            obstacles=CommandButton("柱 / 实际障碍", "GYOBSTACLES");closures=CommandButton("实际封闭物", "GYCLOSURES");
            ButtonRow(boundaryCard,obstacles,closures);
            tips.SetToolTip(doors,"每个门在图上捕捉两个门边点。可连续选多个，也可追加、替换或清空。");
            tips.SetToolTip(passages,"选择房间内的闭合多段线。保留区可通行，柜体不得占用。");
            tips.SetToolTip(closures,"仅选实际存在的隔板、墙或固定封闭轮廓，用于检查能否绕过鞋柜。");

            var schemes=Card(work,"5  在实际边界内生成、调整、入图","先完成设置预览并选择边界，再比较实际布置方案。",boldFont);
            schemeSection=schemes;
            generate=CommandButton("生成布置方案", "GYGENERATE",true);Add(schemes,generate);Add(schemes,workflowHint);Add(schemes,candidates);Add(schemes,candidateDetail);
            preview=CommandButton("显示所选方案", "GYPREVIEW");cancel=CommandButton("取消预览", "GYCANCEL");ButtonRow(schemes,preview,cancel);
            sourceInfo.Padding=new Padding(8);sourceInfo.BackColor=Color.FromArgb(255,246,226);Add(schemes,sourceInfo);
            rebuild=CommandButton("使用图片重建块（待比对）", "GYREBUILD");bind=CommandButton("绑定原 CAD 设备块", "GYBLOCK");Add(schemes,rebuild);Add(schemes,bind);
            tips.SetToolTip(rebuild,"没有原始 CAD 图块时，按已知尺寸和图片建立设备块。来源仍标为据图重建，需要图面比对。");
            check=CommandButton("检查当前方案", "GYCHECK");commit=CommandButton("确认当前方案入图", "GYCOMMIT",true);ButtonRow(schemes,check,commit);

            var editing=Card(edit,"调整当前房间设备","可编辑预览中的设备，或已经确认入图的设备。",boldFont);
            ButtonRow(editing,CommandButton("拖动设备 / 柜组", "GYDRAG"),CommandButton("旋转 90°", "GYROTATE"));
            ButtonRow(editing,CommandButton("换型号 / 单双柜", "GYTYPE"),CommandButton("删除设备", "GYDELETE"));
            ButtonRow(editing,CommandButton("锁定 / 解锁", "GYLOCK"),CommandButton("挂架改长度", "GYLENGTH"));
            ButtonRow(editing,CommandButton("指定跨柜位置", "GYCROSS"),CommandButton("确认连接柜组", "GYGROUP"));
            ButtonRow(editing,CommandButton("移入当前房间", "GYTRANSFER"),CommandButton("添加辅助设备", "GYADD"));
            var library=Card(edit,"设备图块","优先使用原 CAD 图块；图片重建会保留待比对标识。",boldFont);
            ButtonRow(library,CommandButton("绑定图中原块", "GYBLOCK"),CommandButton("导入 DWG / DXF", "GYIMPORT"));
            Add(library,CommandButton("按图片重建设备块（待比对）", "GYREBUILD"));Add(library,CommandButton("记录原块 / 重建图面比对", "GYVERIFY"));
            var report=Card(edit,"统计与问题","目标、名义容量、实际有效容量分别计算。",boldFont);Add(report,statistics);
            ButtonRow(report,CommandButton("检查并定位问题", "GYCHECK"),CommandButton("定位具体设备", "GYLOCATE"));
            ButtonRow(report,CommandButton("查看各房间统计", "GYSTATS"),CommandButton("导出本地数据", "GYEXPORT"));
            var roomTools=Card(edit,"房间管理","新增实例保留独立范围，不改已有房间。",boldFont);
            ButtonRow(roomTools,CommandButton("新建房间", "GYNEWROOM"),CommandButton("当前房间面积预估", "GYESTIMATE"));

            var common=Card(settings,"本房间的空间参数","毫米。预排通道是规划假设，实际操作深度需按现场核对。",boldFont);
            NumberRow(common,"预排通道 mm",aisle,"人员净宽 mm",walkWidth);NumberRow(common,"实际操作深度 mm",operation,"柜后安装距 mm",rear);
            Add(common,Text("实际操作深度填 0 表示尚未核对。一更柜间通道至少1100 mm；两侧共用同一净距。"));Add(common,orth);Add(common,actionsVerified);
            shoeSettings=Card(settings,"换鞋柜参数","柜宽650 mm（含中间50 mm通风道），转角R650。S24每侧3层；S48每侧6层。",boldFont);
            NumberRow(shoeSettings,"鞋柜到边界 mm",margin);Add(shoeSettings,Text("允许的单柜型号（不会拉伸设备）："));
            foreach(string sku in new[]{"S12","S18","S24"})shoeTypes.Items.Add(sku,true);Add(shoeSettings,shoeTypes);
            tips.SetToolTip(stack,"每个48人双柜必须由两个24人单柜相邻夹住：24—48—24。禁止连续48、端部48，以及用12/18或转角代替相邻24；S48自身不作为跨坐位置。");
            hangerSettings=Card(settings,"二更挂架参数","每段分别扣端距；650 mm 是吊杆构件尺寸，不是安装标高。",boldFont);
            NumberRow(hangerSettings,"最长单段 mm",maxSegment,"挂钩节距 mm",pitch);NumberRow(hangerSettings,"前端退距 mm",startMargin,"后端退距 mm",endMargin);
            NumberRow(hangerSettings,"分段间距 mm",segmentGap,"安装标高 mm",elevation);Add(hangerSettings,Text("安装标高填 0 表示待核；450 mm 只表示已知架体投影。"));Add(hangerSettings,installationVerified);
            Add(settings,ActionButton("保存参数并重算面积建议",()=>ApplyInputs(),true));

            rooms.SelectedIndexChanged+=(_,__)=>
            {
                if(updating || !(rooms.SelectedItem is RoomChoice r))return;
                if(!Queue("GYROOM "+r.Id))
                {
                    // A validation error must not leave the visible room different from the active room.
                    updating=true;try{rooms.SelectedIndex=rooms.Items.Cast<RoomChoice>().ToList().FindIndex(c=>c.Id==boundRoomId);}finally{updating=false;}
                }
            };
            rectangleChoices.SelectedIndexChanged+=(_,__)=>
            {
                ShowSelectedEstimate();
                if(!updating && !refreshingEstimate && planningRoom!=null && estimate!=null)
                    try{UpdateWorkflowForInputs(ReadPopulation(),ReadPlanningRoom());}catch(ArgumentException){ }
            };
            candidates.DrawItem+=DrawCandidate;
            candidates.SelectedIndexChanged+=(_,__)=>
            {
                if(updating || candidates.SelectedIndex<0)return;
                try
                {
                    int index=candidates.SelectedIndex;if(!ApplyInputs())return;
                    var s=Host.Active;s.Room.SelectedCandidate=index;Queue("GYPREVIEW");
                }
                catch(System.Exception ex){ShowError(ex);}
            };
            foreach(var n in new[]{shift,male,aisle,margin,operation,rear,maxSegment,pitch,startMargin,endMargin,elevation,walkWidth,segmentGap,customPeople})
                n.ValueChanged+=(_,__)=>InputsChanged();
            shiftCount.SelectedIndexChanged+=(_,__)=>InputsChanged();
            foreach(var c in new[]{orth,actionsVerified,installationVerified})c.CheckedChanged+=(_,__)=>InputsChanged();
            noStack.CheckedChanged+=(_,__)=>{if(!updating && noStack.Checked){preferStackedOption=false;InputsChanged();}};
            stack.CheckedChanged+=(_,__)=>{if(!updating && stack.Checked){preferStackedOption=true;InputsChanged();}};
            shoeTypes.ItemCheck+=(_,__)=>
            {
                if(!updating && surface.IsHandleCreated)surface.BeginInvoke(new Action(InputsChanged));
            };
            estimateTimer.Tick+=(_,__)=>{estimateTimer.Stop();RefreshEstimate();};
            tips.SetToolTip(pendingInfo,"人数及参数修改后，面积建议立即更新。开始定框、生成、编辑或切房间时会自动保存这些参数。");
        }
        private TableLayoutPanel Page(string title)
        {
            var tab=new TabPage(title){BackColor=Background,AutoScroll=true,Padding=new Padding(10)};
            var body=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,BackColor=Background,Margin=Padding.Empty};
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));tab.Controls.Add(body);tabs.TabPages.Add(tab);return body;
        }
        private static TableLayoutPanel Card(TableLayoutPanel parent,string title,string hint,Font bold)
        {
            var card=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,BackColor=Color.White,Padding=new Padding(12),Margin=new Padding(0,0,0,10)};
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));var heading=Text(title,true);heading.Font=bold;Add(card,heading);Add(card,Text(hint));Add(parent,card);return card;
        }
        private static void Add(TableLayoutPanel parent,Control child)
        {
            int row=parent.RowCount++;parent.RowStyles.Add(new RowStyle(SizeType.AutoSize));child.Dock=DockStyle.Fill;
            if(child is Label label){label.MaximumSize=new Size(400,0);parent.SizeChanged+=(_,__)=>label.MaximumSize=new Size(Math.Max(100,parent.ClientSize.Width-parent.Padding.Horizontal),0);}
            if(child.Margin==Padding.Empty)child.Margin=new Padding(0,0,0,6);
            parent.Controls.Add(child,0,row);
        }
        private static TableLayoutPanel NumberRow(TableLayoutPanel parent,string title,NumericUpDown value,string? secondTitle=null,NumericUpDown? second=null)
        {
            var row=new TableLayoutPanel{AutoSize=true,ColumnCount=second==null?1:2,Margin=new Padding(0,0,0,7)};
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,second==null?100:50));if(second!=null)row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            void Field(string label,NumericUpDown number,int column)
            {
                var cell=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,Margin=new Padding(column==0?0:6,0,column==0 && second!=null?6:0,0)};
                cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Add(cell,Text(label));number.Margin=Padding.Empty;Add(cell,number);row.Controls.Add(cell,column,0);
            }
            Field(title,value,0);if(second!=null)Field(secondTitle!,second,1);Add(parent,row);return row;
        }
        private static void ButtonRow(TableLayoutPanel parent,params Button[] buttons)
        {
            var row=new TableLayoutPanel{Height=40,ColumnCount=buttons.Length,Margin=new Padding(0,0,0,5)};
            for(int i=0;i<buttons.Length;i++){row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/buttons.Length));buttons[i].Margin=new Padding(i==0?0:4,0,i==buttons.Length-1?0:4,0);row.Controls.Add(buttons[i],i,0);}Add(parent,row);
        }
        private Button CommandButton(string text,string command,bool primary=false)=>ActionButton(text,()=>Queue(command),primary);
        private static Button ActionButton(string text,Action action,bool primary=false)
        {
            var b=new Button{Text=text,Height=36,Dock=DockStyle.Fill,FlatStyle=FlatStyle.Flat,BackColor=primary?Blue:Color.FromArgb(245,247,251),ForeColor=primary?Color.White:Ink,Cursor=Cursors.Hand,Margin=new Padding(0,0,0,6),UseVisualStyleBackColor=false};
            b.FlatAppearance.BorderColor=primary?Blue:Color.FromArgb(213,221,233);b.Click+=(_,__)=>action();return b;
        }
        private Population ReadPopulation()=>new Population{ShiftCount=shiftCount.SelectedIndex+1,PeoplePerShift=(int)shift.Value,MaleRatio=male.Value/100};
        private Room ReadPlanningRoom()
        {
            if(planningRoom==null)throw new ArgumentException("请先打开图纸并选择房间。");
            var r=CopyPlanningRoom(planningRoom);var o=r.Options;
            o.PlanningAisleMm=(double)aisle.Value;o.ShoeBoundaryMm=(double)margin.Value;o.OperationDepthMm=operation.Value==0?(double?)null:(double)operation.Value;o.RearGapMm=(double)rear.Value;
            o.HangerMaxSegmentMm=(double)maxSegment.Value;o.HookPitchMm=(int)pitch.Value;o.HookStartMarginMm=(int)startMargin.Value;o.HookEndMarginMm=(int)endMargin.Value;
            o.WalkWidthMm=(double)walkWidth.Value;o.HangerSegmentGapMm=(double)segmentGap.Value;o.InstallationElevationMm=elevation.Value==0?(double?)null:(double)elevation.Value;
            o.AllowStacking=stack.Checked;o.Orthogonal=orth.Checked;o.DoorAndOperationVerified=actionsVerified.Checked;o.InstallationVerified=installationVerified.Checked;
            o.ShoeCapacities=shoeTypes.CheckedItems.Cast<string>().Select(id=>int.Parse(id.Substring(1))).ToList();
            if(r.Gender=="custom")r.CustomPeople=(int)customPeople.Value;
            return r;
        }
        private static Room CopyPlanningRoom(Room r)=>new Room{Id=r.Id,Name=r.Name,Mode=r.Mode,Gender=r.Gender,CustomPeople=r.CustomPeople,Options=Serialization.Copy(r.Options),Boundary=r.Boundary,PlanningPreviewSignature=r.PlanningPreviewSignature,PlanningPreviewOption=r.PlanningPreviewOption};
        private void InputsChanged()
        {
            if(updating)return;inputsDirty=true;pendingInfo.Text="人数 / 参数已修改，面积建议正在更新…";pendingInfo.ForeColor=Blue;
            estimateTimer.Stop();estimateTimer.Start();
        }
        private void RefreshEstimate()
        {
            try
            {
                int selected=Math.Max(0,rectangleChoices.SelectedIndex);var draft=ReadPlanningRoom();var population=ReadPopulation();estimate=AreaPlanning.Estimate(population,draft);
                int stackedOption=estimate.Options.FindIndex(o=>o.Items.Any(i=>i.EquipmentId=="S48"));
                if(restoredPlanningOption>=0){selected=restoredPlanningOption;restoredPlanningOption=-1;}
                else if(preferStackedOption){selected=Math.Max(0,stackedOption);preferStackedOption=false;}
                refreshingEstimate=true;rectangleChoices.BeginUpdate();
                try
                {
                    rectangleChoices.Items.Clear();foreach(var option in estimate.Options)rectangleChoices.Items.Add(option);
                    if(rectangleChoices.Items.Count>0)rectangleChoices.SelectedIndex=Math.Min(selected,rectangleChoices.Items.Count-1);
                }
                finally{rectangleChoices.EndUpdate();refreshingEstimate=false;}
                stackingStatus.Text=stack.Checked && stackedOption<0?"当前人数或参数没有合法的交错叠柜预估，下面仅提供单柜参考。":stack.Checked?"允许叠放时先显示含双柜的交错参考；也可切换全单柜方案比较。":"当前按不叠放预估，所有鞋柜均为单柜。";
                areaSummary.Text=estimate.Options.Count==0?estimate.Message:$"目标 {estimate.Target} 人 · 参考房间 {estimate.Options.Min(o=>o.AreaM2):0.##}–{estimate.Options.Max(o=>o.AreaM2):0.##} ㎡";
                assumptions.Text=estimate.Basis+"\r\n"+estimate.Assumptions+(estimate.Message.Length>0?"\r\n"+estimate.Message:"");
                tips.SetToolTip(areaSummary,assumptions.Text);inputError.Text="";ShowSelectedEstimate();
                pendingInfo.Text=inputsDirty?"面积已按新输入更新，下一步操作会自动保存。":"修改人数会立即更新面积建议；操作前自动保存参数。";
                pendingInfo.ForeColor=inputsDirty?Blue:Muted;
                if(inputsDirty){footerTitle.Text=$"新目标 {estimate.Target} 人 · 原方案待重算";footerDetail.Text="人数或叠放设置修改后，请先重新预览。";}
                UpdateWorkflowForInputs(population,draft);
            }
            catch(System.Exception ex)
            {
                estimate=null;planningPreviewCurrent=false;rectangleChoices.Items.Clear();sketch.ShowPlan(null);areaSummary.Text="请先修正人数或参数";areaDetail.Text="";inputError.Text=ex.Message;
                planPreview.Enabled=suggested.Enabled=manualRectangle.Enabled=existingRectangle.Enabled=generate.Enabled=nextStep.Enabled=preview.Enabled=commit.Enabled=false;
            }
        }
        private void ShowSelectedEstimate()
        {
            var option=rectangleChoices.SelectedItem as RectangleEstimate;sketch.ShowPlan(option);
            areaDetail.Text=option==null?"":$"名义 {option.Nominal} 人位 · 设备投影 {option.ProjectionM2:0.##} ㎡\r\n预排包络；门柱、辅助设备、衣物与操作另核。";
            if(option!=null && planningRoom?.Mode==RoomMode.Shoe)areaDetail.Text+="\r\n鞋柜宽650 mm，含中间50 mm通风道；转角R650。";
            planPreview.Enabled=option!=null && lastSession!=null;
            suggested.Enabled=option!=null && lastSession!=null && planningPreviewCurrent;
        }
        private void UpdateWorkflowForInputs(Population population,Room draft)
        {
            var s=lastSession;bool hasOptions=estimate!=null && estimate.Options.Count>0;
            planningPreviewCurrent=AreaPlanning.IsPreviewCurrent(population,draft,rectangleChoices.SelectedIndex);
            planPreview.Enabled=s!=null && hasOptions;
            existingRectangle.Enabled=manualRectangle.Enabled=s!=null && planningPreviewCurrent;
            suggested.Enabled=s!=null && planningPreviewCurrent && hasOptions;
            generate.Enabled=s?.Room.Boundary!=null && planningPreviewCurrent && s.Room.ConstraintIssue.Length==0;
            preview.Enabled=s!=null && s.Room.Candidates.Count>0 && planningPreviewCurrent && !inputsDirty;
            candidates.Enabled=planningPreviewCurrent && !inputsDirty;
            commit.Enabled=candidateCanCommit && planningPreviewCurrent && !inputsDirty;
            rebuild.Enabled=bind.Enabled=planningPreviewCurrent && !inputsDirty;
            planningPreviewStatus.Text=planningPreviewCurrent?(s?.Room.Boundary==null?"预览已完成 · 现在可以选择自己的房间边界。":"预览已完成 · 已选实际边界，可继续生成和调整。"):
                "先点击上方预览；修改人数、叠放、参数或切换参考方案后，需要重新预览。";
            planningPreviewStatus.ForeColor=planningPreviewCurrent?Color.FromArgb(39,119,77):Blue;
            nextStep.Enabled=s!=null && (planningPreviewCurrent || hasOptions);
            if(s==null)return;
            if(!planningPreviewCurrent){nextStep.Text="下一步 · 预览设备与参考尺寸";workflowHint.Text="先完成当前人数及叠放设置的预览，再选择实际边界。";return;}
            if(s.Room.Boundary==null){nextStep.Text="下一步 · 选择我的房间边界";workflowHint.Text="预览完成。请选择自己的闭合不规则边界或矩形。";return;}
            if(s.Room.ConstraintIssue.Length>0){nextStep.Text="修正房间边界或通行区";workflowHint.Text=s.Room.ConstraintIssue;return;}
            if(s.Room.Doors.Count==0 && s.Room.Candidates.Count==0)
            {
                nextStep.Text="下一步 · 设置出入口";workflowHint.Text="在墙上选一个或多个出入口，也可补选通行保留区。";return;
            }
            workflowHint.Text=s.Room.Stale?"边界或现场条件已变化，请重新生成或检查方案。":"在真实边界内生成布置，点选方案即可预览并调整。";
            nextStep.Text=s.Room.Stale || s.Room.Candidates.Count==0?"下一步 · 在实际边界内生成方案":
                !s.Room.PreviewVisible?(s.Room.Placed.Count>0?"检查已入图设备":"下一步 · 预览所选方案"):
                rebuild.Visible?"下一步 · 建立设备图块（待比对）":commit.Enabled?"下一步 · 确认方案入图":"下一步 · 检查并修正方案";
        }
        private bool Queue(string command)
        {
            var doc=CadApplication.DocumentManager.MdiActiveDocument;if(doc==null)return false;
            if(boundDocument!=null && doc!=boundDocument){Update(Host.Active);return false;}
            if(doc.CommandInProgress.Length>0){doc.Editor.WriteMessage("\n请先完成或取消当前 CAD 命令。");return false;}
            if(!ApplyInputs())return false;
            if(command=="GYGENERATE" || command=="GYPREVIEW" || command=="GYCOMMIT")
            {
                RefreshEstimate();
                if(!planningPreviewCurrent){StartPlanningPreview();return false;}
                if(command=="GYGENERATE" && !generate.Enabled)return false;
                if(command=="GYCOMMIT" && !commit.Enabled)return false;
            }
            doc.SendStringToExecute(command+" ",true,false,false);return true;
        }
        private void StartSuggested()
        {
            // Apply first: changed people/SKUs can reduce or reorder the available dimensions.
            if(!ApplyInputs())return;RefreshEstimate();int selected=rectangleChoices.SelectedIndex;
            if(!planningPreviewCurrent){StartPlanningPreview();return;}
            if(selected>=0)Queue("GYRECT Suggested "+(selected+1));
        }
        private void StartPlanningPreview()
        {
            if(!ApplyInputs())return;RefreshEstimate();int selected=rectangleChoices.SelectedIndex;
            if(selected>=0)Queue("GYPLAN "+(selected+1));
        }
        private void SelectBoundary(string command)
        {
            if(!ApplyInputs())return;RefreshEstimate();
            if(!planningPreviewCurrent){StartPlanningPreview();return;}
            Queue(command);
        }
        private void NextStep()
        {
            if(!ApplyInputs())return;RefreshEstimate();var s=lastSession;if(s==null)return;
            if(!planningPreviewCurrent){StartPlanningPreview();return;}
            if(s.Room.Boundary==null){Queue("GYRECT Select");return;}
            if(s.Room.ConstraintIssue.Length>0){Queue(s.Room.ConstraintIssue.StartsWith("通行保留区",StringComparison.Ordinal)?"GYPASSAGES":"GYRECT Select");return;}
            if(s.Room.Doors.Count==0 && s.Room.Candidates.Count==0){Queue("GYDOORS");return;}
            if(s.Room.Stale || s.Room.Candidates.Count==0){Queue("GYGENERATE");return;}
            var selected=s.Room.Candidates[Math.Max(0,Math.Min(s.Room.SelectedCandidate,s.Room.Candidates.Count-1))];
            if(!s.Room.PreviewVisible){Queue(s.Room.Placed.Count>0?"GYCHECK":"GYPREVIEW");return;}
            if(CheckBlocks(s,selected).Missing>0){Queue("GYREBUILD");return;}
            Queue(commit.Enabled?"GYCOMMIT":"GYCHECK");
        }
        private sealed class BlockReadiness
        {internal int Missing;internal string Reason="";}
        private static BlockReadiness CheckBlocks(Session s,Candidate candidate)
        {
            var result=new BlockReadiness();
            using(var tx=s.Document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var table=(BlockTable)tx.GetObject(s.Document.Database.BlockTableId,OpenMode.ForRead);
                foreach(var item in candidate.Items)
                    try
                    {
                        var mapping=Blocks.Mapping(s,item);
                        if(!table.Has(mapping.BlockName))throw new ArgumentException("图中已缺少设备块定义："+mapping.BlockName+"。请重新接入或明确重建。");
                    }
                    catch(ArgumentException ex){result.Missing++;if(result.Reason.Length==0)result.Reason=ex.Message;}
            }
            return result;
        }
        private bool ApplyInputs()
        {
            if(!inputsDirty)return true;
            try
            {
                surface.ValidateChildren();var p=ReadPopulation();p.Validate();var draft=ReadPlanningRoom();draft.Options.Validate();p.For(draft);
                var s=Host.Active;if(s.Document!=boundDocument || s.Room.Id!=boundRoomId){Update(s);return false;}
                if(s.Document.CommandInProgress.Length>0)throw new ArgumentException("请先完成当前 CAD 命令。");
                using(s.Document.LockDocument())
                {
                    s.Refresh();
                    s.Edit(_=>
                    {
                        bool optionsChanged=AreaPlanning.Signature(p,s.Room)!=AreaPlanning.Signature(p,draft);
                        s.Project.ChangePopulation(p);s.Room.Options=draft.Options;
                        if(s.Room.Gender=="custom")s.Room.CustomPeople=draft.CustomPeople;
                        if(optionsChanged)s.Room.InvalidateCandidates();
                    });
                    s.Refresh();s.ShowRoomPreview();
                }
                inputsDirty=false;Update(s);return true;
            }
            catch(System.Exception ex){inputError.Text=ex.Message;ShowError(ex);return false;}
        }
        internal void Show(Session s){Update(s);tabs.SelectedTab=workflowPage;workflowPage.AutoScrollPosition=Point.Empty;palette.Visible=true;}
        internal void Update(Session s)
        {
            bool sameContext=boundDocument==s.Document && boundRoomId==s.Room.Id;
            bool preserveInputs=sameContext && inputsDirty;
            bool planningDisplayChanged=observedPlanningPreview!=s.Preview || observedPlanningRevision!=s.Preview.PlanningRevision;
            bool completedPlanningDisplay=planningDisplayChanged && s.Preview.PlanningVisible && s.Preview.PlanningRoomId==s.Room.Id &&
                s.Preview.PlanningSignature==s.Room.PlanningPreviewSignature && s.Room.PlanningPreviewOption>=0;
            bool wasPlanningPreviewCurrent=planningPreviewCurrent;
            string previousPlanningSignature=planningRoom?.PlanningPreviewSignature??"";
            int previousPlanningOption=planningRoom?.PlanningPreviewOption??-1;
            updating=true;
            try
            {
                lastSession=s;boundDocument=s.Document;boundRoomId=s.Room.Id;planningRoom=CopyPlanningRoom(s.Room);
                if(!sameContext)
                {
                    tabs.SelectedTab=workflowPage;workflowPage.AutoScrollPosition=Point.Empty;
                    restoredPlanningOption=s.Room.PlanningPreviewOption;preferStackedOption=restoredPlanningOption<0 && s.Room.Mode==RoomMode.Shoe && s.Room.Options.AllowStacking;
                }
                else if(completedPlanningDisplay)
                {
                    // CLI GYPLAN can choose a different option, or repeat a saved option after
                    // the user changed the combo. Restore only on a new successful display.
                    restoredPlanningOption=s.Room.PlanningPreviewOption;preferStackedOption=false;
                }
                string signature=string.Join(";",s.Project.Rooms.Select(r=>r.Id+":"+r.Name));
                if(signature!=roomList){rooms.Items.Clear();foreach(var room in s.Project.Rooms)rooms.Items.Add(new RoomChoice(room.Id,room.Name));roomList=signature;}
                rooms.SelectedIndex=s.Project.Rooms.FindIndex(r=>r.Id==s.Project.CurrentRoomId);
                if(!preserveInputs)
                {
                    inputsDirty=false;shiftCount.SelectedIndex=s.Project.Population.ShiftCount-1;
                    Set(shift,s.Project.Population.PeoplePerShift);Set(male,s.Project.Population.MaleRatio*100);
                    Set(customPeople,s.Room.CustomPeople??0);
                    var o=s.Room.Options;Set(aisle,(decimal)o.PlanningAisleMm);Set(margin,(decimal)o.ShoeBoundaryMm);Set(operation,(decimal)(o.OperationDepthMm??0));Set(rear,(decimal)o.RearGapMm);
                    Set(maxSegment,(decimal)o.HangerMaxSegmentMm);Set(pitch,o.HookPitchMm);Set(startMargin,o.HookStartMarginMm);Set(endMargin,o.HookEndMarginMm);Set(elevation,(decimal)(o.InstallationElevationMm??0));
                    Set(walkWidth,(decimal)o.WalkWidthMm);Set(segmentGap,(decimal)o.HangerSegmentGapMm);stack.Checked=o.AllowStacking;noStack.Checked=!o.AllowStacking;orth.Checked=o.Orthogonal;actionsVerified.Checked=o.DoorAndOperationVerified;installationVerified.Checked=o.InstallationVerified;
                    for(int i=0;i<shoeTypes.Items.Count;i++)shoeTypes.SetItemChecked(i,o.ShoeCapacities.Contains(int.Parse(((string)shoeTypes.Items[i]).Substring(1))));
                }
                customRow.Visible=s.Room.Gender=="custom";customPeople.Enabled=s.Room.Gender=="custom";
                var population=s.Project.Population;
                legacyPeople.Visible=population.Registered.HasValue;
                legacyPeople.Text=$"旧图记录：在册 {population.Registered}，当班 {population.Shift}，备用 {population.Reserve*100:0.##}%。已按旧当班数推算班次，请核对上方输入。";
                if(s.Room.LegacyCustomPeople.HasValue)legacyPeople.Text+=$" 旧自定义人数 {s.Room.LegacyCustomPeople}。";
                if(s.Room.ReserveOverride.HasValue)legacyPeople.Text+=$" 旧房间备用率 {s.Room.ReserveOverride*100:0.##}%。";
                stackingSettings.Visible=shoeSettings.Visible=s.Room.Mode==RoomMode.Shoe;hangerSettings.Visible=s.Room.Mode==RoomMode.SecondChange;
                int areaStep=s.Room.Mode==RoomMode.Shoe?3:2;
                ((Label)sizingSection.GetControlFromPosition(0,0)).Text=areaStep+"  预览面积、设备与尺寸";
                ((Label)boundarySection.GetControlFromPosition(0,0)).Text=(areaStep+1)+"  选择自己的房间边界";
                ((Label)schemeSection.GetControlFromPosition(0,0)).Text=(areaStep+2)+"  在实际边界内生成、调整、入图";
                var boundary=s.Room.Boundary;
                boundaryInfo.Text=boundary==null?"尚未选边界 · 先完成上方预览，再到图中选择自己的闭合房间边界。":
                    $"实际房间面积  {boundary.AreaM2:0.##} ㎡\r\n外包尺寸  {boundary.Width/1000:0.##} × {boundary.Height/1000:0.##} m（仅辅助尺寸）";
                if(boundary!=null && boundary.CurveToleranceMm>0)boundaryInfo.Text+=$"\r\n弧边按 {boundary.CurveToleranceMm:0.###} mm 保守近似校核。";
                boundarySketch.Visible=boundary!=null;boundarySketch.ShowBoundary(boundary);
                if(s.Room.Placed.Count>0)boundaryInfo.Text+="\r\n可重新选择边界；现有设备保持原位，随后重新校核。";
                if(s.Room.ConstraintIssue.Length>0)boundaryInfo.Text+="\r\n"+s.Room.ConstraintIssue;
                obstacles.Enabled=doors.Enabled=passages.Enabled=closures.Enabled=boundary!=null;closures.Visible=s.Room.Mode==RoomMode.Shoe;
                accessInfo.Visible=boundary!=null;
                accessInfo.Text=$"已选 {s.Room.Doors.Count} 个出入口 · {s.Room.Passages.Count} 个通行保留区";
                if(s.Room.Mode==RoomMode.Shoe && s.Room.Doors.Count>0)
                    accessInfo.Text+=$"（脏侧 {s.Room.Doors.Count(d=>d.Side==DoorSide.Dirty)} / 净侧 {s.Room.Doors.Count(d=>d.Side==DoorSide.Clean)}）";
                var accessIssue=s.Room.Statistics.Issues.FirstOrDefault(i=>i.Code=="DOOR_INVALID" || i.Code=="PASSAGE_INVALID");
                if(accessIssue!=null)accessInfo.Text+="\r\n"+accessIssue.Message;
                string cs=string.Join(";",s.Room.Candidates.Select(c=>c.Id+":"+c.Family+":"+c.Statistics.Summary));
                string ids=string.Join(";",s.Room.Candidates.Select(c=>c.Id));bool newSchemes=sameContext && ids.Length>0 && ids!=candidateIds;candidateIds=ids;
                if(candidateSignature!=boundRoomId+cs){candidates.Items.Clear();foreach(var c in s.Room.Candidates)candidates.Items.Add(c);candidateSignature=boundRoomId+cs;}
                if(s.Room.Candidates.Count>0)candidates.SelectedIndex=Math.Max(0,Math.Min(s.Room.SelectedCandidate,s.Room.Candidates.Count-1));
                var selected=s.Room.Candidates.Count==0?null:s.Room.Candidates[s.Room.SelectedCandidate];bool hasCandidate=selected!=null;
                preview.Enabled=commit.Enabled=hasCandidate;cancel.Enabled=s.Room.PreviewVisible;check.Enabled=boundary!=null;
                if(selected!=null)
                {
                    string counts=string.Join(" · ",selected.Items.GroupBy(i=>i.EquipmentId).Select(g=>EquipmentPresentation.Describe(g.First(),s.Room.Options).Title+" × "+g.Count()));
                    int single=selected.Items.Count(i=>i.Equipment.IsShoe && i.EquipmentId!="S48"),doubled=selected.Items.Count(i=>i.EquipmentId=="S48");
                    candidateDetail.Text=counts+(s.Room.Mode==RoomMode.Shoe?$"\r\n单柜 {single} 组（每侧3层） / 双柜 {doubled} 组（每侧6层） / 箱体 {selected.Statistics.Boxes} 个":"")+
                        $"\r\n目标 {selected.Statistics.Target} / 名义 {selected.Statistics.Nominal} / 有效 {(selected.Statistics.Effective.HasValue?selected.Statistics.Effective.Value.ToString():"待核")}";
                    var readiness=CheckBlocks(s,selected);int missing=readiness.Missing;
                    bool pending=selected.Statistics.Checks.TryGetValue("source_block",out var source) && source!=CheckState.Pass;
                    sourceInfo.Visible=missing>0 || pending;
                    sourceInfo.Text=missing>0?$"下一步：{missing} 件设备的图块尚未就绪，需接入 / 修正后才能入图。\r\n"+readiness.Reason:pending?"设备图块尚待比对。可在「设备与图块」记录比对；来源与现场操作条件分别校核。":"";
                    rebuild.Visible=bind.Visible=missing>0;rebuild.Enabled=bind.Enabled=true;
                    bool badGeometry=selected.Statistics.Checks.TryGetValue("geometry",out var geometry) && geometry==CheckState.Fail;
                    bool badBoundary=selected.Statistics.Checks.TryGetValue("boundary",out var edge) && edge==CheckState.Fail;
                    bool badStacking=selected.Statistics.Checks.TryGetValue(ShoeStacking.CheckKey,out var stackingState) && stackingState==CheckState.Fail;
                    bool badLockerAisle=selected.Statistics.Checks.TryGetValue(LockerAisles.CheckKey,out var lockerAisleState) && lockerAisleState==CheckState.Fail;
                    if(badStacking)candidateDetail.Text+="\r\n交错规则未满足：每个48人柜的两端都须相接24人单柜。";
                    if(badLockerAisle)candidateDetail.Text+="\r\n一更衣柜间通道净距不足1100 mm，请调整柜排。";
                    bool badAccess=RoomAccess.Failed(selected.Statistics);
                    if(badAccess)candidateDetail.Text+="\r\n有出入口或通行保留区未通过校核。";
                    candidateCanCommit=selected.Items.Count>0 && missing==0 && !badGeometry && !badBoundary && !badStacking && !badLockerAisle && !badAccess;
                    tips.SetToolTip(commit,badStacking?"每个48人柜两端必须是相接的24人单柜，先修正交错布置。":badLockerAisle?"一更两侧衣柜之间需留至少1100 mm净空。":missing>0?"先接入设备图块，才能确认入图。":badGeometry || badBoundary?"先修正碰撞、尺寸或边距问题。":"待核条件会在入图时要求明确作为草案确认。");
                }
                else{candidateCanCommit=false;candidateDetail.Text="尚无方案。预览并选择实际边界后，点击“生成布置方案”。";sourceInfo.Visible=false;rebuild.Visible=bind.Visible=false;}
                var stats=s.Room.PreviewVisible && selected!=null?selected.Statistics:s.Room.Statistics;
                SetStatistics(stats,s.Room.PreviewVisible?"所选方案预览":"已入图设备");
                RefreshEstimate();
                observedPlanningPreview=s.Preview;observedPlanningRevision=s.Preview.PlanningRevision;
                bool newlyPlanned=sameContext && planningPreviewCurrent && (completedPlanningDisplay || !wasPlanningPreviewCurrent || previousPlanningSignature!=s.Room.PlanningPreviewSignature || previousPlanningOption!=s.Room.PlanningPreviewOption);
                if(newlyPlanned && surface.IsHandleCreated)surface.BeginInvoke(new Action(()=>{tabs.SelectedTab=workflowPage;workflowPage.ScrollControlIntoView(boundarySection);}));
                if(newSchemes && surface.IsHandleCreated)surface.BeginInvoke(new Action(()=>{tabs.SelectedTab=workflowPage;workflowPage.ScrollControlIntoView(schemeSection);}));
            }
            finally{updating=false;}
        }
        private void DrawCandidate(object? sender,DrawItemEventArgs e)
        {
            if(e.Index<0 || e.Index>=candidates.Items.Count)return;
            var c=(Candidate)candidates.Items[e.Index];bool selected=(e.State&DrawItemState.Selected)!=0;
            using(var background=new SolidBrush(selected?Color.FromArgb(231,239,254):Color.White))e.Graphics.FillRectangle(background,e.Bounds);
            var color=selected?Blue:Ink;
            TextRenderer.DrawText(e.Graphics,$"方案 {e.Index+1}  ·  {c.Family}",surface.Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y+5,e.Bounds.Width-16,20),color,TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);
            string detail=$"名义 {c.Statistics.Nominal} / 有效 {(c.Statistics.Effective.HasValue?c.Statistics.Effective.Value.ToString():"待核")} / 条件缺口 {c.Statistics.ConditionalDeficit}";
            TextRenderer.DrawText(e.Graphics,detail,surface.Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y+27,e.Bounds.Width-16,19),Muted,TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);
            e.DrawFocusRectangle();
        }
        internal void SetStatistics(Statistics stats,string prefix="拖动预检")
        {
            footerTitle.Text=$"目标 {stats.Target}  ·  名义 {stats.Nominal}  ·  有效 {(stats.Effective.HasValue?stats.Effective.Value.ToString():"待核")}";
            footerDetail.Text=$"{prefix.Trim()}  /  条件缺口 {stats.ConditionalDeficit} 人  /  设备投影 {stats.ProjectionM2:0.##} ㎡";
            bool badStacking=stats.Checks.TryGetValue(ShoeStacking.CheckKey,out var state) && state==CheckState.Fail;
            bool badLockerAisle=stats.Checks.TryGetValue(LockerAisles.CheckKey,out var lockerAisleState) && lockerAisleState==CheckState.Fail;
            if(badStacking)footerDetail.Text+="\r\n48人柜两邻24人柜：未通过，请修正24—48—24布置。";
            if(badLockerAisle)footerDetail.Text+="\r\n一更柜间通道：不足1100 mm，请调整柜排。";
            bool badAccess=RoomAccess.Failed(stats);
            if(badAccess)footerDetail.Text+="\r\n出入口或通行保留区未通过校核。";
            footerDetail.ForeColor=badStacking || badLockerAisle || badAccess?Color.Firebrick:Muted;
            statistics.Text=prefix.Trim()+"\r\n"+footerTitle.Text+"\r\n"+
                $"地面柜组 {stats.Groups} / 箱体 {stats.Boxes} / 转角 {stats.Corners}\r\n挂架 {stats.HangerSegments} 段 / 总长 {stats.HangerLengthMm:0.###} mm\r\n"+
                (stats.NominalShoeCells>0?$"名义储格 {stats.NominalShoeCells}（每人2格）\r\n":"")+
                string.Join("\r\n",stats.Checks.Where(c=>(c.Key!=ShoeStacking.CheckKey || lastSession?.Room.Mode==RoomMode.Shoe) &&
                    (c.Key!=LockerAisles.CheckKey || lastSession?.Room.Mode==RoomMode.FirstChange)).Select(c=>CheckName(c.Key)+"："+StateName(c.Value)))+"\r\n"+
                string.Join("\r\n",stats.Issues.Take(16).Select(i=>i.Message+(i.InstanceId.Length>0?" ["+i.InstanceId.Substring(0,Math.Min(8,i.InstanceId.Length))+"]":"")));
        }
        private static string CheckName(string key)=>key switch
        {
            "geometry"=>"碰撞与尺寸","boundary"=>"房间边界与边距","connections"=>"柜组连接",ShoeStacking.CheckKey=>"48人柜两邻24人柜",LockerAisles.CheckKey=>"一更柜间净距至少1100 mm","front_access"=>"操作侧可达",
            RoomAccess.EntryCheck=>"出入口可达",RoomAccess.ClearanceCheck=>"出入口与保留区净空","hygiene_without_crossing"=>"实体脏净分隔","walking_without_crossing"=>"人员无法绕行",
            "workflow_with_crossing"=>"跨柜转换流程","source_block"=>"设备图块比对","operation"=>"实际操作空间","installation"=>"挂架安装条件","auxiliary_operations"=>"辅助设备操作",_=>key
        };
        private static string StateName(CheckState state)=>state switch{CheckState.Pass=>"通过",CheckState.Fail=>"未通过",CheckState.RequiresInput=>"待补充 / 核对",CheckState.ApproximateOnly=>"仅近似预检",_=>"尚未检查"};
        private static void Set(NumericUpDown control,decimal value)=>control.Value=Math.Min(control.Maximum,Math.Max(control.Minimum,value));
        private static void ShowError(System.Exception ex)=>MessageBox.Show(ex.Message,"GENGYI",MessageBoxButtons.OK,MessageBoxIcon.Information);
        public void Dispose(){estimateTimer.Dispose();tips.Dispose();palette.Dispose();boldFont.Dispose();}
        private sealed class RoomChoice
        {internal string Id;private readonly string name;internal RoomChoice(string id,string name){Id=id;this.name=name;}public override string ToString()=>name;}
    }
}
