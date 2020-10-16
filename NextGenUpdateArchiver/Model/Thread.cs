using System;
using System.Collections.Generic;
using System.Text;

namespace NextGenUpdateArchiver.Model
{
    public class Thread
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public Dictionary<int, Post> Posts { get; set; }
        public Profile Poster { get; set; }
        public DateTime StartDate { get; set; }
        public int Views { get; set; }
        public int Replies { get; set; }
        public bool Closed { get; set; }
        public bool Stickied { get; set; }
    }
}
