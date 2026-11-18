using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Gengyi.Core;

namespace Gengyi.Cad.AutoCAD2024
{
    // A temporary planning envelope: cancellation never creates database entities.
    internal sealed class SuggestedRectangleJig : DrawJig, IDisposable
    {
        private readonly RectangleEstimate estimate;
        private readonly Database database;
        private readonly LayoutOptions options;
        private readonly Frame frame;
        private readonly List<Entity> graphics=new List<Entity>();
        private bool rotated;
        private Point3d? last;
        internal SuggestedRectangleJig(RectangleEstimate estimate,Matrix3d ucs,double units,Database database,LayoutOptions options)
        {
            this.estimate=estimate;this.database=database;this.options=Serialization.Copy(options);
            frame=new Frame{Origin=CadGeometry.A(ucs.CoordinateSystem3d.Origin),
                XAxis=CadGeometry.A(ucs.CoordinateSystem3d.Xaxis.GetNormal()),YAxis=CadGeometry.A(ucs.CoordinateSystem3d.Yaxis.GetNormal()),
                MillimetersPerDrawingUnit=units};
        }
        internal Boundary Boundary()=>new Boundary{Width=rotated?estimate.HeightMm:estimate.WidthMm,
            Height=rotated?estimate.WidthMm:estimate.HeightMm,Frame=Serialization.Copy(frame)};
        internal bool Rotated=>rotated;
        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options=new JigPromptPointOptions("\n移动预览矩形，单击定框 [旋转90度(Rotate)]，Esc取消：");
            options.Keywords.Add("Rotate");options.UserInputControls=UserInputControls.Accept3dCoordinates;
            var value=prompts.AcquirePoint(options);
            if(value.Status==PromptStatus.Keyword){rotated=!rotated;last=null;return SamplerStatus.OK;}
            if(value.Status!=PromptStatus.OK)return SamplerStatus.Cancel;
            if(last.HasValue && last.Value.DistanceTo(value.Value)<1e-8)return SamplerStatus.NoChange;
            last=value.Value;frame.Origin=CadGeometry.A(value.Value);return SamplerStatus.OK;
        }
        protected override bool WorldDraw(WorldDraw draw)
        {
            Clear();var b=Boundary();
            var labelRoom=new Room{Boundary=b,Options=options};
            graphics.Add(CadGeometry.PreviewPolyline(database,Gengyi.Core.Geometry.Rect(0,0,b.Width,b.Height),frame,3));
            foreach(var item in estimate.Items)
            {
                // Rotate the physical instance, so its outline and labels share one transform.
                // Reuse immutable local hooks/outline without mutating the selected estimate.
                var shown=new Placement{EquipmentId=item.EquipmentId,Length=item.Length,
                    Position=rotated?new V2(estimate.HeightMm-item.Position.Y,item.Position.X):item.Position,
                    Rotation=item.Rotation+(rotated?90:0),LocalHooks=item.LocalHooks,PhysicalOutline=item.PhysicalOutline};
                graphics.Add(CadGeometry.PreviewPolyline(database,Gengyi.Core.Geometry.Outline(shown),frame,8));
                foreach(var text in Blocks.Labels(database,labelRoom,shown))graphics.Add(text);
            }
            double height=Math.Max(120,Math.Min(b.Width,b.Height)/45);
            AddText($"{b.Width/1000:0.##} × {b.Height/1000:0.##} m   {estimate.AreaM2:0.##} ㎡",b.Height+height*1.8,height);
            AddText(estimate.Name+" · 面积预排 / 待现场校核",b.Height+height*.3,height*.7);
            foreach(var entity in graphics)draw.Geometry.Draw(entity);
            return true;
        }
        private void AddText(string text,double y,double height)
        {
            var entity=new DBText();entity.SetDatabaseDefaults(database);
            entity.Position=new Point3d(0,y,0);entity.Height=height;entity.TextString=text;entity.ColorIndex=3;
            entity.TransformBy(CadGeometry.ToWorld(frame));graphics.Add(entity);
        }
        private void Clear(){foreach(var entity in graphics)entity.Dispose();graphics.Clear();}
        public void Dispose()=>Clear();
    }
}
