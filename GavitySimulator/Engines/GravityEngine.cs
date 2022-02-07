using GravitySimulator.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Engines
{
    public class GravityEngine
    {
        const float G = 0.00000000006673f;//6.0 * Math.Pow(10.0, -11);

        private static GravityEngine engine = new GravityEngine();
        public static GravityEngine Instance
        {
            get { return engine; }
        }

        public Vector GetCenterOfMass()
        {
            return GetCenterOfMass(Points);
        }

        public Vector GetCenterOfMass(List<PointInSpace> points)
        {
            float mx = 0;
            float my = 0;
            float mz = 0;

            float mt = 0;

            foreach (var pt in points)
            {
                mx += pt.Mass * pt.Position.x;
                my += pt.Mass * pt.Position.y;
                mz += pt.Mass * pt.Position.z;
                mt += pt.Mass;
            }

            Vector v = new Vector
            {
                x = mx / mt,
                y = my / mt,
                z = mz / mt
            };

            return v;
        }

        public List<PointInSpace> Points = new List<PointInSpace>();
        public Dictionary<int, List<PointInSpace>> PointLogs = new Dictionary<int, List<PointInSpace>>();



        public void SetupPoints(int count, float maxX = 100.0f, float maxY = 100.0f, float maxZ = 100.0f, float maxRadius = 7.5f, float density = 103877746.0f, float initialVelocity = 2.0f
            , Vector velocityOverride = null, Vector positionOverride = null)
        {
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;

            Points = new List<PointInSpace>();
            PointLogs = new Dictionary<int, List<PointInSpace>>();
            Random randm = new Random(DateTime.Now.Millisecond);
            for (int i = 0; i < count; i++)
            {
                //float density = 103877746.0; //23877746.0;
                float radius = Convert.ToSingle(randm.NextDouble()) * maxRadius; //15.0

                if (radius < maxRadius / 3f)
                {
                    radius = maxRadius / 3f;
                }

                var Velocity = new Vector
                {
                    x = Convert.ToSingle(randm.NextDouble()) * initialVelocity - (initialVelocity / 2.0f),
                    y = Convert.ToSingle(randm.NextDouble()) * initialVelocity - (initialVelocity / 2.0f),
                    z = Convert.ToSingle(randm.NextDouble()) * initialVelocity - (initialVelocity / 2.0f)
                };
                if (velocityOverride != null)
                    Velocity = velocityOverride;
                //Velocity = new Vector();

                float scalePosX = MaxX - (5.0f * maxRadius);
                float scalePosY = MaxY - (5.0f * maxRadius);
                float scalePosZ = MaxZ - (5.0f * maxRadius);

                PointInSpace p = new PointInSpace()
                {
                    Id = i + 1,
                    Mass = density * GetVolumeFromRadius(radius),
                    TimeStamp = 0,
                    IsAlive = true,
                    Radius = radius,
                    Force = new Vector { x = 0, y = 0, z = 0 },
                    Velocity = Velocity,
                    Position = new Vector
                    {
                        x = Convert.ToSingle(randm.NextDouble()) * scalePosX + maxRadius,
                        y = Convert.ToSingle(randm.NextDouble()) * scalePosY + maxRadius,
                        z = Convert.ToSingle(randm.NextDouble()) * scalePosZ + maxRadius
                    }
                };

                if (positionOverride != null)
                    p.Position = positionOverride;

                Points.Add(p);
                PointLogs.Add(p.Id, new List<PointInSpace> { p });
            }
        }

        float GetVolumeFromRadius(float radius)
        {
            float V = 4.0f / 3.0f * Convert.ToSingle(Math.Pow(radius, 3)) * Convert.ToSingle(Math.PI);
            return V;
        }

        public void CalculateForces(float globalGravity = 0)
        {
            var totalPoints = Points.Where(x => x.IsAlive);

            foreach (var point in totalPoints)
            {
                point.Force = new Vector { x = 0, y = 0, z = 0 };
                if (!point.WallCollision)
                {
                    point.Force += new Vector { z = -globalGravity * point.Mass };
                }
                foreach (var otherpoint in Points.Where(x => x.Id != point.Id && x.IsAlive))
                {
                    Vector F = CalculateGravitationalForceBetweenTwoObjects(point, otherpoint);
                    point.Force += F;


                }
            }
        }

        public bool IsProcessing { get; set; }

        public float MaxX = 100.0f;
        public float MaxY = 100.0f;
        public float MaxZ = 100.0f;

        Vector ProcessWallCollision(PointInSpace point, float wallElasticity)
        {
            point.WallCollision = false;
            Vector position = point.Position;
            Vector velocity = point.Velocity;
            float radius = point.Radius;

            if (wallElasticity < 0) wallElasticity = 0;
            if (wallElasticity > 1) wallElasticity = 1;

            if ((position.x - radius < 0 && velocity.x < 0) || (position.x + radius > MaxX && velocity.x > 0))
            {
                velocity.x = -velocity.x * wallElasticity;
                point.WallCollision = true;
            }

            if ((position.y - radius < 0 && velocity.y < 0) || (position.y + radius > MaxY && velocity.y > 0))
            {
                velocity.y = -velocity.y * wallElasticity;
                point.WallCollision = true;
            }

            if ((position.z - radius < 0 && velocity.z < 0) || (position.z + radius > MaxZ && velocity.z > 0))
            {
                velocity.z = -velocity.z * wallElasticity;
                point.WallCollision = true;
            }

            return velocity;
        }

        public void SenseCollisions(float wallElasticity, float funLevel, bool wallCollisions)
        {
            if (funLevel < 1) funLevel = 1; //level 1 senses collisions exactly, > 1 will reduce collision radius to allow for higher forces to occur, with more orbits
            var newpoints = new List<PointInSpace>();
            var cnt = 1;
            foreach (var point in Points.Where(x => x.IsAlive))
            {
                if (!point.IsAlive) { continue; }

                if (wallCollisions)
                {
                    point.Velocity = ProcessWallCollision(point, wallElasticity);
                }

                foreach (var otherpoint in Points.Where(x => x.Id != point.Id && x.IsAlive))
                {
                    var dist = GetDistanceBetweenTwoPoints(point.Position, otherpoint.Position) * funLevel;
                    if (dist <= (point.Radius + otherpoint.Radius))
                    {
                        point.IsAlive = false;
                        otherpoint.IsAlive = false;
                        var newpoint = new PointInSpace()
                        {
                            Id = Points.Max(x => x.Id) + cnt,
                            IsAlive = true,
                            Mass = point.Mass + otherpoint.Mass,
                            Radius = GetRadiusFromVolume(GetVolumeFromRadius(point.Radius) + GetVolumeFromRadius(otherpoint.Radius)),
                            Force = point.Force + otherpoint.Force,
                            TimeStamp = point.TimeStamp,
                            Position = GetCenterOfMass(new List<PointInSpace> { point, otherpoint }),
                            Velocity = (point.Velocity * point.Mass + otherpoint.Velocity * otherpoint.Mass) / (point.Mass + otherpoint.Mass),
                            PointCollision = true
                        };
                        newpoints.Add(newpoint);
                        cnt++;
                    }
                }
            }

            Points.AddRange(newpoints);
            foreach (var newp in newpoints)
            {
                PointLogs.Add(newp.Id, new List<PointInSpace> { newp });
            }
        }

        private float GetRadiusFromVolume(float volume)
        {
            float radius = Convert.ToSingle(Math.Pow(volume * (3.0f / 4.0f) * (1.0f / Convert.ToSingle(Math.PI)), (1.0f / 3.0f)));

            return radius;
        }

        public void CalculateVelocities(float timeskip)
        {
            foreach (var point in Points.Where(x => x.IsAlive))
            {
                Vector newvelocity = CalculateVelocityDueToForceAppliedOverTime(point.Mass, timeskip, point.Force, point.Velocity);
                point.Velocity = newvelocity;
            }
        }

        public void CalculateNewPositions(float timeskip)
        {
            foreach (var point in Points.Where(x => x.IsAlive))
            {
                var lastpoint = PointLogs[point.Id].LastOrDefault();
                Vector newposition = CalculatePositionDueToVelocityOverTime(timeskip, point.Position, point.Velocity);
                point.Position = newposition;



                point.TimeStamp += timeskip;
                PointLogs[point.Id].Add(new PointInSpace { Id = point.Id, Force = point.Force, Mass = point.Mass, Position = point.Position, TimeStamp = point.TimeStamp, Velocity = point.Velocity });
                PointLogs[point.Id].Remove(PointLogs[point.Id].FirstOrDefault());
            }
        }

        private Vector CalculatePositionDueToVelocityOverTime(float timeskip, Vector position, Vector velocity)
        {
            if (position == null)
            {
                position = new Vector { x = 0, y = 0, z = 0 };
            }

            position += velocity * timeskip;

            return position;
        }

        float GetDistanceBetweenTwoPoints(Vector point, Vector otherpoint)
        {
            var r = Convert.ToSingle(Math.Sqrt(
               Convert.ToSingle(Math.Pow((otherpoint.x - point.x), 2.0f)) + Convert.ToSingle(Math.Pow((otherpoint.y - point.y), 2.0f)) + Convert.ToSingle(Math.Pow((otherpoint.z - point.z), 2.0f))
                ));
            return r;
        }

        public Vector CalculateGravitationalForceBetweenTwoObjects(PointInSpace point, PointInSpace otherpoint)
        {
            var r = GetDistanceBetweenTwoPoints(point.Position, otherpoint.Position);

            var F = G * point.Mass * otherpoint.Mass / Convert.ToSingle(Math.Pow(r, 2.0));

            Vector v = new Vector();

            float dxoverr = (otherpoint.Position.x - point.Position.x) / r;
            float dyoverr = (otherpoint.Position.y - point.Position.y) / r;
            float dzoverr = (otherpoint.Position.z - point.Position.z) / r;

            v.x = dxoverr * F;
            v.y = dyoverr * F;
            v.z = dzoverr * F;

            return v;
        }

        public Vector CalculateVelocityDueToForceAppliedOverTime(float mass, float time, Vector force, Vector velocity)
        {
            if (velocity == null)
            {
                velocity = new Vector { x = 0, y = 0, z = 0 };
            }

            velocity.x += force.x * time / mass;
            velocity.y += force.y * time / mass;
            velocity.z += force.z * time / mass;

            return velocity;
        }

        public void ProcessPositions(int steps, float timeskip, float globalGravity, float wallElasticity, float funLevel, bool wallCollisions)
        {
            if (!Instance.IsProcessing)
            {
                Instance.IsProcessing = true;
                for (int i = 0; i < steps; i++)
                {
                    Instance.SenseCollisions(wallElasticity, funLevel, wallCollisions);
                    Instance.CalculateForces(globalGravity);
                    Instance.CalculateVelocities(timeskip);
                    Instance.CalculateNewPositions(timeskip);
                }
                Instance.IsProcessing = false;
            }
        }
    }
}
