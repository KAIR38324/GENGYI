using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Gengyi.Core;

namespace Gengyi.Cad.AutoCAD2024
{
    // A scaled planning thumbnail. This never creates, selects or edits a CAD entity.
    internal sealed class PlanningSketch : Control
    {
        private RectangleEstimate? plan;
        private readonly List<(PointF[] points, string label, bool stacked)> shapes=new List<(PointF[],string,bool)>();
        internal PlanningSketch()
        {
            DoubleBuffered=true;Height=146;BackColor=Color.FromArgb(246,248,252);
            SetStyle(ControlStyles.ResizeRedraw,true);
        }
        internal void ShowPlan(RectangleEstimate? value)
        {
            plan=value;shapes.Clear();
            if(value!=null)
                foreach(var p in value.Items)
                    shapes.Add((Gengyi.Core.Geometry.Outline(p).Select(v=>new PointF((float)v.X,(float)v.Y)).ToArray(),
                        p.EquipmentId=="S48"?"双柜":p.EquipmentId=="CORNER_R600"?"":p.EquipmentId=="LOCKER12"?"4层":
                        p.Equipment.IsShoe?"3层":"挂架",p.EquipmentId=="S48"));
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var ink=new SolidBrush(Color.FromArgb(77,91,113)))
            using(var line=new Pen(Color.FromArgb(83,112,157),1.3f))
            using(var fill=new SolidBrush(Color.FromArgb(203,220,244)))
            using(var stackedFill=new SolidBrush(Color.FromArgb(221,210,247)))
            using(var dashed=new Pen(Color.FromArgb(178,191,212),1){DashStyle=DashStyle.Dash})
            {
                if(plan==null || plan.WidthMm<=0 || plan.HeightMm<=0)
                {
                    TextRenderer.DrawText(g,"输入有效人数后显示尺寸示意",Font,ClientRectangle,Color.FromArgb(108,121,141),
                        TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;
                }
                float usableW=Math.Max(1,Width-80),usableH=Math.Max(1,Height-42);
                double scale=Math.Min(usableW/plan.WidthMm,usableH/plan.HeightMm);
                float w=(float)(plan.WidthMm*scale),h=(float)(plan.HeightMm*scale);
                float x=42+(usableW-w)/2,y=8+(usableH-h)/2;
                g.FillRectangle(Brushes.White,x,y,w,h);g.DrawRectangle(line,x,y,w,h);
                foreach(var shape in shapes)
                {
                    var points=shape.points.Select(p=>new PointF(x+(float)(p.X*scale),y+h-(float)(p.Y*scale))).ToArray();
                    if(points.Length<3)continue;
                    g.FillPolygon(shape.stacked?stackedFill:fill,points);g.DrawPolygon(line,points);
                    float left=points.Min(p=>p.X),right=points.Max(p=>p.X),top=points.Min(p=>p.Y),bottom=points.Max(p=>p.Y);
                    if(right-left>35 && bottom-top>14 && shape.label.Length>0)
                        TextRenderer.DrawText(g,shape.label,Font,new Rectangle((int)left,(int)top,(int)(right-left),(int)(bottom-top)),
                            Color.FromArgb(50,69,98),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
                }
                g.DrawLine(dashed,x,y+h+10,x+w,y+h+10);
                TextRenderer.DrawText(g,$"{plan.WidthMm/1000:0.##} m",Font,new Rectangle((int)x,(int)(y+h+12),(int)w,22),
                    Color.FromArgb(77,91,113),TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding);
                var saved=g.Save();g.TranslateTransform(x-28,y+h/2);g.RotateTransform(-90);
                string height=$"{plan.HeightMm/1000:0.##} m";var size=g.MeasureString(height,Font);
                g.DrawString(height,Font,ink,-size.Width/2,-size.Height/2);g.Restore(saved);
            }
        }
    }
}
