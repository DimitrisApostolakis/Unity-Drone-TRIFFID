using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class Properties
{
    [JsonProperty("class")] public string className;
    public string id;
    public float? confidence;
    public string category;
    public string source;
    public float? altitude_m;

    [JsonProperty("marker-color", NullValueHandling = NullValueHandling.Ignore)]
    public string marker_color;

    [JsonProperty("fill", NullValueHandling = NullValueHandling.Ignore)]
    public string fill;

    [JsonProperty("fill-opacity", NullValueHandling = NullValueHandling.Ignore)]
    public float? fill_opacity;
}

public class Geometry
{
    public string type;
    public JToken coordinates;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<Geometry> geometries;
}

public class Feature
{
    public string type = "Feature";
    public string id;
    public Properties properties;
    public Geometry geometry;
}

public class Root
{
    [JsonProperty("type", Order = -2)]
    public string type = "FeatureCollection";

    [JsonProperty("lastModified", Order = -1, NullValueHandling = NullValueHandling.Ignore)]
    public string lastModified;

    public List<Feature> features;
}

[Serializable]
public class PrefabEntry
{
    public string name;
    public GameObject prefab;
}

public class TransformConfig
{
    public OriginWgs84 origin_wgs84;
    public ColmapToEnu colmap_to_enu;
}

public class OriginWgs84
{
    public double lat;
    public double lon;
    public double alt;
}

public class ColmapToEnu
{
    public float scale;
    public float[] R_rowmajor;
    public float[] t;
}

[Serializable]
public class MqttLatestStamp
{
    public int sec;
    public int nanosec;
}

[Serializable]
public class MqttLatestHeader
{
    public MqttLatestStamp stamp;

    [JsonProperty("frame_id")]
    public string frame_id;
}

[Serializable]
public class MqttLatestMessage
{
    public MqttLatestHeader header;
    public double? roll;
    public double? pitch;
    public double? yaw;
    public double? latitude;
    public double? longitude;
    public double? battery_temperature;
    public double? power_voltage;
    public double? power_current;
    public double? temperature_ntc1;
    public double? temperature_ntc2;
    public int? mode;
    public string commander;
    public string behavior;
    public string action_command;
}

[Serializable]
public class MqttLatestResponse
{
    public MqttLatestMessage message;
    public string topic;
    public string time;
}
