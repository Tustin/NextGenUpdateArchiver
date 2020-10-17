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
        public string Slug { get; set; }
        public int PageCount { get; set; }
        public bool IsCategory { get; set; }
        public List<Forum> SubForums { get; set; } = new List<Forum>();
        // We are only saving the thread ids here because storing a whole thread object for each thread will be very consuming.
        public List<int> ThreadsIds { get; set; } = new List<int>();

    }
}
