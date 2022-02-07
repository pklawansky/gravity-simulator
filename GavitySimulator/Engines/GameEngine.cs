using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Engines
{
    public class GameEngine
    {
        public GameEngine()
        {

        }

        public virtual bool OnUserCreate()
        {
            return true;
        }

        public virtual bool OnUserUpdate(float fElapsedTime)
        {
            return true;
        }
    }
}
