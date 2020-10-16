using System;
using System.Collections.Generic;
using System.Text;

namespace NextGenUpdateArchiver.Model
{
    public class Forum
    {
        public Forum(int id)
        {
            Id = id;
        }

        public int Id { get; set; }
        public string Name { get; set; }
        public List<Forum> SubForums { get; set; }
        public int PageCount { get; set; }
        public bool IsCategory { get; set; }
        public List<Thread> Threads { get; set; }
    }
}
