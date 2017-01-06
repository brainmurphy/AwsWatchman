namespace Watchman.Configuration
{
    public class Queue
    {
        public string Name { get; set; }

        public string Pattern { get; set; }

        public int? LengthThreshold { get; set; }

        public int? OldestMessageThreshold { get; set; }

        public ErrorQueue Errors { get; set; }

        public static implicit operator Queue(string text)
        {
            return new Queue
            {
                Name = text
            };
        }

        public override string ToString()
        {
            return Name ?? Pattern;
        }
    }
}
