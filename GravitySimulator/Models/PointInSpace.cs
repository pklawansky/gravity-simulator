using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models
{
    public class PointInSpace
    {
        public int Id { get; set; }
        public bool IsAlive { get; set; }

        public float Mass { get; set; }
        public float TimeStamp { get; set; }

        public float Radius { get; set; }

        public Vector Force { get; set; }
        public Vector Position { get; set; }
        public Vector Velocity { get; set; }
        public bool WallCollision { get; set; }
        public bool PointCollision { get; set; }
        public IModel Model { get; set; }

        public override string ToString()
        {
            return string.Format("Id = {0}, Mass = {1}, Force [{2}], Position [{3}], Velocity [{4}]",
                this.Id, this.Mass, this.Force.ToString(), this.Position.ToString(), this.Velocity.ToString());
        }
    }
}
