using System;
using System.Collections.Generic;

namespace NextGenUpdateArchiver.Model
{
    public class Profile
    {
        public string Username { get; set; }

        public string Usertitle { get; set; }

        public int Reputation { get; set; }

        public DateTime JoinDate { get; set; }

        public Dictionary<int, VisitorMessage> VisitorMessages { get; set; } // Laterz

        public List<int> RecentVisitors { get; set; }
    }
}