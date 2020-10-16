using System;
using System.Collections.Generic;

namespace NextGenUpdateArchiver.Model
{
    public class Post
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public int UserId { get; set; }
        public string Contents { get; set; }
        public List<string> Thanks { get; set; } = new List<string>();
        public DateTime PostDate { get; set; }
    }
}