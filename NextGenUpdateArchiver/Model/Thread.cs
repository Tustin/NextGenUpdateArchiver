using System;
using System.Collections.Generic;
using System.Text;

namespace NextGenUpdateArchiver.Model
{
    public class Thread
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public List<Post> Posts { get; set; } = new List<Post>();
        public Profile Poster { get; set; }
        public DateTime StartDate { get; set; }
        public int Views { get; set; }
        public bool Closed { get; set; }
        public bool Stickied { get; set; }
        public int PageCount { get; set; }
    }
}
