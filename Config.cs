using Newtonsoft.Json;

namespace DouyinRtmp;

public class Config
{
    [JsonProperty("obs_path")]
    public string ObsPath { get; set; } = string.Empty;
}