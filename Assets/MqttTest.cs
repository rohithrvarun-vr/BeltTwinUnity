using UnityEngine;
using MQTTnet;

public class MqttTest : MonoBehaviour
{
    void Start()
    {
        Debug.Log("MQTTnet loaded: " + typeof(MqttFactory).FullName);
    }
}