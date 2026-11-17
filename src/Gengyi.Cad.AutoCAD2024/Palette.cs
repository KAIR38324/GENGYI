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
        private readonly NumericUpDown registered=Number(10000,600),shift=Number(10000,300),male=Number(100,50),reserve=Number(100,0);
        private readonly NumericUpDown aisle=Number(10000,1000),margin=Number(10000,1000),operation=Number(10000,0),rear=Number(1000,0),maxSegment=Number(50000,3000);
        private readonly NumericUpDown pitch=Number(10000,100),startMargin=Number(10000,50),endMargin=Number(10000,50),elevation=Number(20000,0);
        private readonly NumericUpDown walkWidth=Number(2000,600),segmentGap=Number(10000,100),customPeople=Number(10000,0),roomReserve=Number(100,0);
        private readonly CheckBox reserveOverride=Check("本房间使用独立备用率"),stack=Check("允许局部双柜叠放（S48）"),orth=Check("沿房间方向正交拖动、旋转");
        private readonly CheckBox actionsVerified=Check("已实际核对柜门、更衣动作和操作深度"),installationVerified=Check("已实际核对吊挂标高、支撑及衣物占用");
        private readonly CheckedListBox shoeTypes=new CheckedListBox{Height=68,CheckOnClick=true,BorderStyle=BorderStyle.FixedSingle};
        private readonly ComboBox rectangleChoices=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,IntegralHeight=false,DropDownHeight=160};
        private readonly PlanningSketch sketch=new PlanningSketch();
        private readonly ListBox candidates=new ListBox{Height=154,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=50,BorderStyle=BorderStyle.FixedSingle,IntegralHeight=false};
        private readonly TextBox statistics=new TextBox{Height=260,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.FixedSingle,BackColor=Color.White};
        private readonly Label areaSummary=Text("正在计算尺寸建议…",true),areaDetail=Text(""),assumptions=Text(""),boundaryInfo=Text("尚未确定房间范围"),sourceInfo=Text(""),candidateDetail=Text(""),footerTitle=Text("先选房间，输入人数",true),footerDetail=Text("面积建议会在定框前显示");
        private readonly Label pendingInfo=Text("修改人数会立即更新面积建议；操作前自动保存参数。"),workflowHint=Text(""),inputError=Text("");
        private readonly Button nextStep,suggested,manualRectangle,existingRectangle,generate,commit,check,preview,cancel,obstacles,seeds,closures,rebuild,bind;
        private readonly TableLayoutPanel shoeSettings,hangerSettings;
        private readonly TableLayoutPanel schemeSection;
        private readonly TabPage workflowPage;
        private readonly TableLayoutPanel customRow;
        private readonly ToolTip tips=new ToolTip{AutoPopDelay=20000,InitialDelay=300,ReshowDelay=100};
        private readonly System.Windows.Forms.Timer estimateTimer=new System.Windows.Forms.Timer{Interval=160};
        private readonly Font boldFont;
        private bool updating,inputsDirty;
        private string boundRoomId="",roomList="",candidateSignature="",candidateIds="";
        private Autodesk.AutoCAD.ApplicationServices.Document? boundDocument;
        private Room? planningRoom;
        private AreaEstimate? estimate;
        private Session? lastSession;
        private static NumericUpDown Number(int max,int value)=>new NumericUpDown{Maximum=max,Minimum=0,Value=value,Dock=DockStyle.Fill,Height=28,ThousandsSeparator=true};
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
            nextStep=ActionButton("下一步 · 选择我的边界",NextStep,true);Add(footer,nextStep);
            surface.Controls.Add(tabs);surface.Controls.Add(footer);surface.Controls.Add(header);palette.Add("布置工作台",surface);
            var work=Page("布置");var edit=Page("设备与图块");var settings=Page("高级参数");
            workflowPage=(TabPage)work.Parent;

            var people=Card(work,"1  人数","鞋位和一更按在册人数；二更按当班人数。",boldFont);
            NumberRow(people,"在册人数",registered,"当班人数",shift);NumberRow(people,"男性比例 %",male,"项目备用 %",reserve);
            customRow=NumberRow(people,"当前自定义房人数",customPeople);Add(people,pendingInfo);inputError.ForeColor=Color.Firebrick;Add(people,inputError);
            var area=Card(work,"2  选择自己的边界 / 参考面积","自己的边界优先；面积与建议框只作规划参考。",boldFont);
            existingRectangle=ActionButton("选择我画的矩形边界",()=>Queue("GYRECT Select"),true);Add(area,existingRectangle);
            manualRectangle=ActionButton("手动点两个角确定范围",()=>Queue("GYRECT Corners"));Add(area,manualRectangle);
            areaSummary.Font=boldFont;Add(area,areaSummary);Add(area,rectangleChoices);Add(area,sketch);Add(area,areaDetail);
            var assumptionToggle=new LinkLabel{Text="查看估算条件",AutoSize=true,LinkColor=Blue,Margin=new Padding(0,0,0,6)};
            assumptionToggle.LinkClicked+=(_,__)=>{assumptions.Visible=!assumptions.Visible;assumptionToggle.Text=assumptions.Visible?"收起估算条件":"查看估算条件";};
            Add(area,assumptionToggle);assumptions.Visible=false;Add(area,assumptions);
            suggested=ActionButton("按建议尺寸创建参考框",StartSuggested);Add(area,suggested);
            boundaryInfo.BackColor=Color.FromArgb(245,247,251);boundaryInfo.Padding=new Padding(8);Add(area,boundaryInfo);
            obstacles=CommandButton("门口 / 障碍", "GYOBSTACLES");seeds=CommandButton("入口 / 出口", "GYSEEDS");closures=CommandButton("实际封闭物", "GYCLOSURES");ButtonRow(area,obstacles,seeds,closures);
            tips.SetToolTip(obstacles,"选择门扇、门口、柱和不可占区的实际闭合轮廓。不会从截图猜测门位。");
            tips.SetToolTip(seeds,"在可行走位置点选入口。换鞋间需要分别点选脏侧入口和净侧出口。");
            tips.SetToolTip(closures,"仅选实际存在的隔板、墙或固定封闭轮廓，用于检查能否绕过鞋柜。");

            var schemes=Card(work,"3  生成、比较、确认","先预览，再确认入图；取消预览不会增加设备。",boldFont);
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
            Add(common,Text("实际操作深度填 0 表示尚未核对。"));Add(common,orth);Add(common,actionsVerified);
            var roomPeople=Card(settings,"本房间备用人数","默认沿用项目备用率。勾选后只影响当前房间。",boldFont);Add(roomPeople,reserveOverride);NumberRow(roomPeople,"本房间备用率 %",roomReserve);
            shoeSettings=Card(settings,"换鞋柜参数","S24 单柜每侧3层；S48 上下两柜每侧共6层。",boldFont);
            NumberRow(shoeSettings,"鞋柜到边界 mm",margin);Add(shoeSettings,stack);Add(shoeSettings,Text("允许的单柜型号（不会拉伸设备）："));
            foreach(string sku in new[]{"S12","S18","S24"})shoeTypes.Items.Add(sku,true);Add(shoeSettings,shoeTypes);
            tips.SetToolTip(stack,"S48 为上下两柜、48人、96格、高1200mm；不自动作为600mm跨坐位置。");
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
            rectangleChoices.SelectedIndexChanged+=(_,__)=>ShowSelectedEstimate();
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
            foreach(var n in new[]{registered,shift,male,reserve,aisle,margin,operation,rear,maxSegment,pitch,startMargin,endMargin,elevation,walkWidth,segmentGap,customPeople,roomReserve})
                n.ValueChanged+=(_,__)=>InputsChanged();
            foreach(var c in new[]{reserveOverride,stack,orth,actionsVerified,installationVerified})c.CheckedChanged+=(_,__)=>InputsChanged();
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
        private Population ReadPopulation()=>new Population{Registered=(int)registered.Value,Shift=(int)shift.Value,MaleRatio=male.Value/100,Reserve=reserve.Value/100};
        private Room ReadPlanningRoom()
        {
            if(planningRoom==null)throw new ArgumentException("请先打开图纸并选择房间。");
            var r=CopyPlanningRoom(planningRoom);var o=r.Options;
            o.PlanningAisleMm=(double)aisle.Value;o.ShoeBoundaryMm=(double)margin.Value;o.OperationDepthMm=operation.Value==0?(double?)null:(double)operation.Value;o.RearGapMm=(double)rear.Value;
            o.HangerMaxSegmentMm=(double)maxSegment.Value;o.HookPitchMm=(int)pitch.Value;o.HookStartMarginMm=(int)startMargin.Value;o.HookEndMarginMm=(int)endMargin.Value;
            o.WalkWidthMm=(double)walkWidth.Value;o.HangerSegmentGapMm=(double)segmentGap.Value;o.InstallationElevationMm=elevation.Value==0?(double?)null:(double)elevation.Value;
            o.AllowStacking=stack.Checked;o.Orthogonal=orth.Checked;o.DoorAndOperationVerified=actionsVerified.Checked;o.InstallationVerified=installationVerified.Checked;
            o.ShoeCapacities=shoeTypes.CheckedItems.Cast<string>().Select(id=>int.Parse(id.Substring(1))).ToList();
            r.ReserveOverride=reserveOverride.Checked?roomReserve.Value/100:(decimal?)null;if(r.Gender=="custom")r.CustomPeople=(int)customPeople.Value;
            return r;
        }
        private static Room CopyPlanningRoom(Room r)=>new Room{Id=r.Id,Name=r.Name,Mode=r.Mode,Gender=r.Gender,CustomPeople=r.CustomPeople,ReserveOverride=r.ReserveOverride,Options=Serialization.Copy(r.Options),Boundary=r.Boundary};
        private void InputsChanged()
        {
            if(updating)return;inputsDirty=true;roomReserve.Enabled=reserveOverride.Checked;pendingInfo.Text="人数 / 参数已修改，面积建议正在更新…";pendingInfo.ForeColor=Blue;
            estimateTimer.Stop();estimateTimer.Start();
        }
        private void RefreshEstimate()
        {
            try
            {
                int selected=Math.Max(0,rectangleChoices.SelectedIndex);estimate=AreaPlanning.Estimate(ReadPopulation(),ReadPlanningRoom());
                rectangleChoices.BeginUpdate();rectangleChoices.Items.Clear();foreach(var option in estimate.Options)rectangleChoices.Items.Add(option);rectangleChoices.EndUpdate();
                if(rectangleChoices.Items.Count>0)rectangleChoices.SelectedIndex=Math.Min(selected,rectangleChoices.Items.Count-1);
                areaSummary.Text=estimate.Options.Count==0?estimate.Message:$"目标 {estimate.Target} 人 · 参考房间 {estimate.Options.Min(o=>o.AreaM2):0.##}–{estimate.Options.Max(o=>o.AreaM2):0.##} ㎡";
                assumptions.Text=estimate.Basis+"\r\n"+estimate.Assumptions+(estimate.Message.Length>0?"\r\n"+estimate.Message:"");
                tips.SetToolTip(areaSummary,assumptions.Text);inputError.Text="";ShowSelectedEstimate();
                nextStep.Enabled=lastSession!=null;
                generate.Enabled=lastSession?.Room.Boundary!=null && lastSession.Room.ConstraintIssue.Length==0;
                pendingInfo.Text=inputsDirty?"面积已按新输入更新，下一步操作会自动保存。":"修改人数会立即更新面积建议；操作前自动保存参数。";
                pendingInfo.ForeColor=inputsDirty?Blue:Muted;
                if(inputsDirty){footerTitle.Text=$"新目标 {estimate.Target} 人 · 原方案待重算";footerDetail.Text="面积建议已更新；生成方案时使用当前输入。";nextStep.Text=planningRoom?.Boundary==null?"下一步 · 选择我的边界":"下一步 · 按新人数生成方案";}
            }
            catch(System.Exception ex)
            {
                estimate=null;rectangleChoices.Items.Clear();sketch.ShowPlan(null);areaSummary.Text="请先修正人数或参数";areaDetail.Text="";inputError.Text=ex.Message;suggested.Enabled=generate.Enabled=nextStep.Enabled=false;
            }
        }
        private void ShowSelectedEstimate()
        {
            var option=rectangleChoices.SelectedItem as RectangleEstimate;sketch.ShowPlan(option);
            areaDetail.Text=option==null?"":$"名义 {option.Nominal} 人位 · 设备投影 {option.ProjectionM2:0.##} ㎡\r\n预排包络；门柱、辅助设备、衣物与操作另核。";
            suggested.Enabled=option!=null && lastSession!=null;
        }
        private bool Queue(string command)
        {
            var doc=CadApplication.DocumentManager.MdiActiveDocument;if(doc==null)return false;
            if(boundDocument!=null && doc!=boundDocument){Update(Host.Active);return false;}
            if(doc.CommandInProgress.Length>0){doc.Editor.WriteMessage("\n请先完成或取消当前 CAD 命令。");return false;}
            if(!ApplyInputs())return false;
            doc.SendStringToExecute(command+" ",true,false,false);return true;
        }
        private void StartSuggested()
        {
            // Apply first: changed people/SKUs can reduce or reorder the available dimensions.
            if(!ApplyInputs())return;RefreshEstimate();int selected=rectangleChoices.SelectedIndex;
            if(selected>=0)Queue("GYRECT Suggested "+(selected+1));
        }
        private void NextStep()
        {
            if(!ApplyInputs())return;var s=lastSession;if(s==null)return;
            if(s.Room.Boundary==null){Queue("GYRECT Select");return;}
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
                    s.Refresh();s.Edit(_=>{s.Project.ChangePopulation(p);s.Room.Options=draft.Options;s.Room.ReserveOverride=draft.ReserveOverride;if(s.Room.Gender=="custom")s.Room.CustomPeople=draft.CustomPeople;s.Room.Stale=true;});s.Refresh();
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
            updating=true;
            try
            {
                lastSession=s;boundDocument=s.Document;boundRoomId=s.Room.Id;planningRoom=CopyPlanningRoom(s.Room);
                if(!sameContext){tabs.SelectedTab=workflowPage;workflowPage.AutoScrollPosition=Point.Empty;}
                string signature=string.Join(";",s.Project.Rooms.Select(r=>r.Id+":"+r.Name));
                if(signature!=roomList){rooms.Items.Clear();foreach(var room in s.Project.Rooms)rooms.Items.Add(new RoomChoice(room.Id,room.Name));roomList=signature;}
                rooms.SelectedIndex=s.Project.Rooms.FindIndex(r=>r.Id==s.Project.CurrentRoomId);
                if(!preserveInputs)
                {
                    inputsDirty=false;Set(registered,s.Project.Population.Registered);Set(shift,s.Project.Population.Shift);Set(male,s.Project.Population.MaleRatio*100);Set(reserve,s.Project.Population.Reserve*100);
                    Set(customPeople,s.Room.CustomPeople??0);Set(roomReserve,(s.Room.ReserveOverride??s.Project.Population.Reserve)*100);reserveOverride.Checked=s.Room.ReserveOverride.HasValue;
                    var o=s.Room.Options;Set(aisle,(decimal)o.PlanningAisleMm);Set(margin,(decimal)o.ShoeBoundaryMm);Set(operation,(decimal)(o.OperationDepthMm??0));Set(rear,(decimal)o.RearGapMm);
                    Set(maxSegment,(decimal)o.HangerMaxSegmentMm);Set(pitch,o.HookPitchMm);Set(startMargin,o.HookStartMarginMm);Set(endMargin,o.HookEndMarginMm);Set(elevation,(decimal)(o.InstallationElevationMm??0));
                    Set(walkWidth,(decimal)o.WalkWidthMm);Set(segmentGap,(decimal)o.HangerSegmentGapMm);stack.Checked=o.AllowStacking;orth.Checked=o.Orthogonal;actionsVerified.Checked=o.DoorAndOperationVerified;installationVerified.Checked=o.InstallationVerified;
                    for(int i=0;i<shoeTypes.Items.Count;i++)shoeTypes.SetItemChecked(i,o.ShoeCapacities.Contains(int.Parse(((string)shoeTypes.Items[i]).Substring(1))));
                }
                customRow.Visible=s.Room.Gender=="custom";customPeople.Enabled=s.Room.Gender=="custom";roomReserve.Enabled=reserveOverride.Checked;
                shoeSettings.Visible=s.Room.Mode==RoomMode.Shoe;hangerSettings.Visible=s.Room.Mode==RoomMode.SecondChange;
                var boundary=s.Room.Boundary;
                boundaryInfo.Text=boundary==null?"尚未选边界 · 点击“选择我画的矩形边界”，到图中点选自己的闭合矩形。":$"已选边界  {boundary.Width/1000:0.##} × {boundary.Height/1000:0.##} m  /  {boundary.Width*boundary.Height/1000000:0.##} ㎡";
                if(s.Room.Placed.Count>0)boundaryInfo.Text+="\r\n可重新选择边界；现有设备保持原位，随后重新校核。";
                if(s.Room.ConstraintIssue.Length>0)boundaryInfo.Text+="\r\n"+s.Room.ConstraintIssue;
                manualRectangle.Enabled=existingRectangle.Enabled=true;
                obstacles.Enabled=seeds.Enabled=closures.Enabled=boundary!=null;closures.Visible=s.Room.Mode==RoomMode.Shoe;generate.Enabled=boundary!=null && s.Room.ConstraintIssue.Length==0;
                workflowHint.Text=boundary==null?"先确定房间范围，再生成方案。":s.Room.Stale?"人数、范围或现场条件已变化，请重新生成或检查方案。":"可生成不同方案；点选下面一项即可预览。";
                string cs=string.Join(";",s.Room.Candidates.Select(c=>c.Id+":"+c.Family+":"+c.Statistics.Summary));
                string ids=string.Join(";",s.Room.Candidates.Select(c=>c.Id));bool newSchemes=sameContext && ids.Length>0 && ids!=candidateIds;candidateIds=ids;
                if(candidateSignature!=boundRoomId+cs){candidates.Items.Clear();foreach(var c in s.Room.Candidates)candidates.Items.Add(c);candidateSignature=boundRoomId+cs;}
                if(s.Room.Candidates.Count>0)candidates.SelectedIndex=Math.Max(0,Math.Min(s.Room.SelectedCandidate,s.Room.Candidates.Count-1));
                var selected=candidates.SelectedItem as Candidate;bool hasCandidate=selected!=null;
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
                    commit.Enabled=missing==0 && !badGeometry && !badBoundary;
                    tips.SetToolTip(commit,missing>0?"先接入设备图块，才能确认入图。":badGeometry || badBoundary?"先修正碰撞、尺寸或边距问题。":"待核条件会在入图时要求明确作为草案确认。");
                }
                else{candidateDetail.Text="尚无方案。定范围后点击“生成布置方案”。";sourceInfo.Visible=false;rebuild.Visible=bind.Visible=false;}
                nextStep.Enabled=true;
                nextStep.Text=boundary==null?"下一步 · 选择我的边界":s.Room.Stale || !hasCandidate?"下一步 · 生成布置方案":
                    !s.Room.PreviewVisible?(s.Room.Placed.Count>0?"检查已入图设备":"下一步 · 预览所选方案"):
                    rebuild.Visible?"下一步 · 建立设备图块（待比对）":commit.Enabled?"下一步 · 确认方案入图":"下一步 · 检查并修正方案";
                var stats=s.Room.PreviewVisible && selected!=null?selected.Statistics:s.Room.Statistics;
                SetStatistics(stats,s.Room.PreviewVisible?"所选方案预览":"已入图设备");
                RefreshEstimate();
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
            statistics.Text=prefix.Trim()+"\r\n"+footerTitle.Text+"\r\n"+
                $"地面柜组 {stats.Groups} / 箱体 {stats.Boxes} / 转角 {stats.Corners}\r\n挂架 {stats.HangerSegments} 段 / 总长 {stats.HangerLengthMm:0.###} mm\r\n"+
                (stats.NominalShoeCells>0?$"名义储格 {stats.NominalShoeCells}（每人2格）\r\n":"")+
                string.Join("\r\n",stats.Checks.Select(c=>CheckName(c.Key)+"："+StateName(c.Value)))+"\r\n"+
                string.Join("\r\n",stats.Issues.Take(16).Select(i=>i.Message+(i.InstanceId.Length>0?" ["+i.InstanceId.Substring(0,Math.Min(8,i.InstanceId.Length))+"]":"")));
        }
        private static string CheckName(string key)=>key switch
        {
            "geometry"=>"碰撞与尺寸","boundary"=>"房间边界与边距","connections"=>"柜组连接","front_access"=>"操作侧可达",
            "entry_reachability"=>"入口可达","hygiene_without_crossing"=>"实体脏净分隔","walking_without_crossing"=>"人员无法绕行",
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
