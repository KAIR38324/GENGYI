using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.AutoCAD.Windows;
using Gengyi.Core;
using CadApplication=Autodesk.AutoCAD.ApplicationServices.Core.Application;

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
        private readonly PaletteSet palette;
        private readonly FlowLayoutPanel body=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(8)};
        private readonly ComboBox rooms=new ComboBox{Width=338,DropDownStyle=ComboBoxStyle.DropDownList};
        private readonly NumericUpDown registered=Number(10000,600),shift=Number(10000,300),male=Number(100,50),reserve=Number(100,0);
        private readonly NumericUpDown aisle=Number(10000,1000),margin=Number(10000,1000),operation=Number(10000,0),rear=Number(1000,0),maxSegment=Number(50000,3000);
        private readonly NumericUpDown pitch=Number(10000,100),startMargin=Number(10000,50),endMargin=Number(10000,50),elevation=Number(20000,0);
        private readonly NumericUpDown walkWidth=Number(2000,600),segmentGap=Number(10000,100);
        private readonly NumericUpDown customPeople=Number(10000,0),roomReserve=Number(100,0);
        private readonly CheckBox reserveOverride=new CheckBox{Text="当前房间使用独立备用率",AutoSize=true};
        private readonly CheckBox stack=new CheckBox{Text="允许局部叠放 S48（不自动跨坐）",AutoSize=true};
        private readonly CheckBox orth=new CheckBox{Text="按房间局部轴正交编辑",AutoSize=true};
        private readonly CheckBox actionsVerified=new CheckBox{Text="已核对实际柜门/更衣动作与操作深度",AutoSize=true};
        private readonly CheckBox installationVerified=new CheckBox{Text="已核对吊挂标高、支撑与衣物占用",AutoSize=true};
        private readonly CheckedListBox shoeTypes=new CheckedListBox{Width=338,Height=55,CheckOnClick=true,MultiColumn=true,ColumnWidth=105};
        private readonly ListBox candidates=new ListBox{Width=338,Height=125,HorizontalScrollbar=true};
        private readonly TextBox statistics=new TextBox{Width=338,Height=185,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
        private bool updating;
        private string roomList="";
        private Autodesk.AutoCAD.ApplicationServices.Document? boundDocument;
        private static NumericUpDown Number(int max,int value)=>new NumericUpDown{Maximum=max,Minimum=0,Value=value,Width=115};
        internal Palette()
        {
            palette=new PaletteSet("GENGYI 更衣设施",new Guid("729EFC43-3F88-41D0-A63C-EA2D873EE988"))
            {MinimumSize=new Size(370,550),Size=new Size(400,850),Style=PaletteSetStyles.ShowCloseButton|PaletteSetStyles.ShowAutoHideButton|PaletteSetStyles.ShowPropertiesMenu};
            var control=new UserControl{Dock=DockStyle.Fill};control.Controls.Add(body);palette.Add("三模式布置",control);
            body.Controls.Add(new Label{Text="房间实例（每间独立保存边界、方案与编辑）",AutoSize=true});body.Controls.Add(rooms);
            rooms.SelectedIndexChanged+=(_,__)=>
            {
                if(updating || rooms.SelectedItem==null)return;
                var room=(RoomChoice)rooms.SelectedItem;Queue("GYROOM "+room.Id);
            };
            Row("在册人数",registered,"当班人数",shift);Row("男比例 %",male,"备用 %",reserve);
            Row("自定义房人数",customPeople,"本房备用 %",roomReserve);body.Controls.Add(reserveOverride);
            Row("预排通道 mm",aisle,"鞋柜边距 mm",margin);Row("实际操作深度",operation,"安装背距 mm",rear);
            Row("挂架最长段",maxSegment,"挂钩节距 mm",pitch);Row("挂架前端距",startMargin,"挂架后端距",endMargin);Row("安装标高 mm",elevation);
            Row("人员宽度 mm",walkWidth,"架段间距 mm",segmentGap);
            body.Controls.Add(new Label{Text="操作深度/标高填 0 表示待核；预排净距是规划假设。",AutoSize=true});
            body.Controls.Add(stack);body.Controls.Add(orth);body.Controls.Add(actionsVerified);body.Controls.Add(installationVerified);
            foreach(string sku in new[]{"S12","S18","S24"})shoeTypes.Items.Add(sku,true);body.Controls.Add(shoeTypes);
            var apply=new Button{Text="应用人数和当前房间参数",Width=338};apply.Click+=(_,__)=>Apply();body.Controls.Add(apply);
            Buttons(("新建房间","GYNEWROOM"),("人数预估","GYESTIMATE"),("选矩形","GYRECT"));
            Buttons(("障碍/门区","GYOBSTACLES"),("固定封闭物","GYCLOSURES"),("脏净种子","GYSEEDS"));
            Buttons(("生成候选","GYGENERATE"),("预览","GYPREVIEW"),("取消预览","GYCANCEL"));
            body.Controls.Add(candidates);
            candidates.SelectedIndexChanged+=(_,__)=>
            {
                if(updating || candidates.SelectedIndex<0)return;
                try{var s=Host.Active;s.Room.SelectedCandidate=candidates.SelectedIndex;Queue("GYPREVIEW");}
                catch(System.Exception ex){ShowError(ex);}
            };
            Buttons(("确认入图","GYCOMMIT"),("拖动/柜组","GYDRAG"),("旋转90°","GYROTATE"));
            Buttons(("型号/叠放","GYTYPE"),("锁定/解锁","GYLOCK"),("删除设备","GYDELETE"));
            Buttons(("挂架改长度","GYLENGTH"),("跨柜点开关","GYCROSS"),("确认连接组","GYGROUP"));
            Buttons(("选取原块","GYBLOCK"),("导入DWG/DXF","GYIMPORT"),("据图重建","GYREBUILD"));
            Buttons(("原块比对确认","GYVERIFY"),("添加辅助设备","GYADD"),("转入本房间","GYTRANSFER"));
            Buttons(("校核","GYCHECK"),("房间统计","GYSTATS"),("异常定位","GYLOCATE"));
            Buttons(("导出本地JSON","GYEXPORT"));
            body.Controls.Add(statistics);
        }
        private void Row(string text,NumericUpDown value,string? other=null,NumericUpDown? second=null)
        {
            var row=new FlowLayoutPanel{Width=345,Height=32,WrapContents=false};
            row.Controls.Add(new Label{Text=text,Width=83,Padding=new Padding(0,5,0,0)});value.Width=75;row.Controls.Add(value);
            if(other!=null && second!=null){row.Controls.Add(new Label{Text=other,Width=83,Padding=new Padding(0,5,0,0)});second.Width=75;row.Controls.Add(second);}body.Controls.Add(row);
        }
        private void Buttons(params (string label,string command)[] choices)
        {
            var row=new FlowLayoutPanel{Width=345,Height=34,WrapContents=false};
            foreach(var choice in choices){var b=new Button{Text=choice.label,Width=106,Height=29};string command=choice.command;b.Click+=(_,__)=>Queue(command);row.Controls.Add(b);}body.Controls.Add(row);
        }
        private void Queue(string command)
        {
            var doc=CadApplication.DocumentManager.MdiActiveDocument;if(doc==null)return;
            if(boundDocument!=null && doc!=boundDocument){Update(Host.Active);return;}
            if(doc.CommandInProgress.Length>0){doc.Editor.WriteMessage("\n请先完成或取消当前 CAD 命令。");return;}
            doc.SendStringToExecute(command+" ",true,false,false);
        }
        private void Apply()
        {
            try
            {
                var s=Host.Active;if(s.Document.CommandInProgress.Length>0)throw new ArgumentException("请先完成当前命令。");
                using(s.Document.LockDocument())
                {
                    s.Refresh();var p=new Population{Registered=(int)registered.Value,Shift=(int)shift.Value,MaleRatio=male.Value/100,Reserve=reserve.Value/100};p.Validate();
                    var o=Serialization.Copy(s.Room.Options);o.PlanningAisleMm=(double)aisle.Value;o.ShoeBoundaryMm=(double)margin.Value;
                    o.OperationDepthMm=operation.Value==0?(double?)null:(double)operation.Value;o.RearGapMm=(double)rear.Value;
                    o.HangerMaxSegmentMm=(double)maxSegment.Value;o.HookPitchMm=(int)pitch.Value;o.HookStartMarginMm=(int)startMargin.Value;o.HookEndMarginMm=(int)endMargin.Value;
                    o.WalkWidthMm=(double)walkWidth.Value;o.HangerSegmentGapMm=(double)segmentGap.Value;
                    o.InstallationElevationMm=elevation.Value==0?(double?)null:(double)elevation.Value;o.AllowStacking=stack.Checked;o.Orthogonal=orth.Checked;
                    o.DoorAndOperationVerified=actionsVerified.Checked;o.InstallationVerified=installationVerified.Checked;
                    o.ShoeCapacities=shoeTypes.CheckedItems.Cast<string>().Select(id=>int.Parse(id.Substring(1))).ToList();o.Validate();
                    s.Edit(_=>{s.Project.ChangePopulation(p);s.Room.Options=o;s.Room.ReserveOverride=reserveOverride.Checked?roomReserve.Value/100:(decimal?)null;if(s.Room.Gender=="custom")s.Room.CustomPeople=(int)customPeople.Value;s.Room.Stale=true;});s.Refresh();Update(s);
                }
            }
            catch(System.Exception ex){ShowError(ex);}
        }
        internal void Show(Session s){Update(s);palette.Visible=true;}
        internal void Update(Session s)
        {
            updating=true;
            try
            {
                boundDocument=s.Document;string signature=string.Join(";",s.Project.Rooms.Select(r=>r.Id+":"+r.Name));
                if(signature!=roomList){rooms.Items.Clear();foreach(var room in s.Project.Rooms)rooms.Items.Add(new RoomChoice(room.Id,room.Name));roomList=signature;}
                rooms.SelectedIndex=s.Project.Rooms.FindIndex(r=>r.Id==s.Project.CurrentRoomId);
                Set(registered,s.Project.Population.Registered);Set(shift,s.Project.Population.Shift);Set(male,s.Project.Population.MaleRatio*100);Set(reserve,s.Project.Population.Reserve*100);
                customPeople.Enabled=s.Room.Gender=="custom";Set(customPeople,s.Room.CustomPeople??0);Set(roomReserve,(s.Room.ReserveOverride??s.Project.Population.Reserve)*100);reserveOverride.Checked=s.Room.ReserveOverride.HasValue;
                var o=s.Room.Options;Set(aisle,(decimal)o.PlanningAisleMm);Set(margin,(decimal)o.ShoeBoundaryMm);Set(operation,(decimal)(o.OperationDepthMm??0));Set(rear,(decimal)o.RearGapMm);
                Set(maxSegment,(decimal)o.HangerMaxSegmentMm);Set(pitch,o.HookPitchMm);Set(startMargin,o.HookStartMarginMm);Set(endMargin,o.HookEndMarginMm);Set(elevation,(decimal)(o.InstallationElevationMm??0));
                Set(walkWidth,(decimal)o.WalkWidthMm);Set(segmentGap,(decimal)o.HangerSegmentGapMm);
                stack.Checked=o.AllowStacking;orth.Checked=o.Orthogonal;actionsVerified.Checked=o.DoorAndOperationVerified;installationVerified.Checked=o.InstallationVerified;
                for(int i=0;i<shoeTypes.Items.Count;i++)shoeTypes.SetItemChecked(i,o.ShoeCapacities.Contains(int.Parse(((string)shoeTypes.Items[i]).Substring(1))));
                candidates.Items.Clear();foreach(var candidate in s.Room.Candidates)candidates.Items.Add(candidate);
                if(s.Room.Candidates.Count>0)candidates.SelectedIndex=Math.Min(s.Room.SelectedCandidate,s.Room.Candidates.Count-1);
                var stats=s.Room.PreviewVisible && candidates.SelectedIndex>=0?s.Room.Candidates[candidates.SelectedIndex].Statistics:s.Room.Statistics;
                SetStatistics(stats,(s.Room.Stale?"参数/现场已变，候选需重核。\r\n":"")+(s.Room.PreviewVisible?"候选预览\r\n":"已入图设备\r\n"));
            }
            finally{updating=false;}
        }
        internal void SetStatistics(Statistics stats,string prefix="拖动预检\r\n") => statistics.Text=prefix+stats.Summary+"\r\n"+
            $"柜组{stats.Groups} / 箱体{stats.Boxes} / 转角{stats.Corners} / 投影{stats.ProjectionM2:0.###}㎡\r\n"+
            $"挂架{stats.HangerSegments}段/{stats.HangerLengthMm:0.###}mm / 辅助{stats.AuxiliaryCount}件\r\n"+
            (stats.NominalShoeCells>0?$"名义储格{stats.NominalShoeCells}（每人2格）\r\n":"")+
            string.Join("\r\n",stats.Checks.Select(c=>c.Key+": "+c.Value))+"\r\n"+string.Join("\r\n",stats.Issues.Take(12).Select(i=>i.InstanceId+" "+i.Message));
        private static void Set(NumericUpDown control,decimal value)=>control.Value=Math.Min(control.Maximum,Math.Max(control.Minimum,value));
        private static void ShowError(System.Exception ex)=>MessageBox.Show(ex.Message,"GENGYI",MessageBoxButtons.OK,MessageBoxIcon.Information);
        public void Dispose()=>palette.Dispose();
        private sealed class RoomChoice
        {internal string Id;private readonly string name;internal RoomChoice(string id,string name){Id=id;this.name=name;}public override string ToString()=>name;}
    }
}
