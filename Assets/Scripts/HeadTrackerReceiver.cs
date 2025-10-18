using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;

public class HeadTrackerReceiver : MonoBehaviour
{

    [System.Serializable]
    private class Payload
    {
        public string dir;
        public string dist;
        public float[] center;
        public float depth;
    }
    private Camera targetCamera;
    UdpClient client;
    IPEndPoint endPoint;

    private float initialY;
    private Vector3 initialPosition;
    private Vector3 initialForward;
    private float targetY;
    private Vector3 targetPosition;

    [Header("Tuning")]
    public float yawRange = 30f;        // +/- độ khi center dịch sang hai bên
    public float closeOffset = 0.5f;    // distance forward khi "Close"
    public float farOffset = -0.5f;     // distance backward khi "Far"
    public float smoothSpeed = 6f;      // smoothing factor

    private float lastCenterX = float.NaN;
    private float lastDepth = float.NaN;

    void Awake()
    {
        targetCamera = Camera.main;
        client = new UdpClient(5005); // Cổng trùng với bên Python
        endPoint = new IPEndPoint(IPAddress.Any, 0);
        Debug.Log("UDP Receiver started on port 5005");
    }
    void Start()
    {
        if (targetCamera != null)
        {
            initialY = targetCamera.transform.eulerAngles.y;
            initialPosition = targetCamera.transform.position;
            initialForward = targetCamera.transform.forward;
            targetY = initialY;
            targetPosition = initialPosition;
        }
    }

    void Update()
    {
        if (client != null && client.Available > 0)
        {
            byte[] data = client.Receive(ref endPoint);
            string message = Encoding.UTF8.GetString(data);
            Debug.Log("UDP recv: " + message);

            // Parse JSON sent from Python
            Payload payload = JsonUtility.FromJson<Payload>(message);
            if (payload != null)
            {
                // nhận mọi payload hợp lệ (không bắt buộc dir/dist phải có)
                UpdateTargetsFromPayload(payload);
            }
            else
            {
                Debug.LogWarning("Invalid payload or missing fields");
            }
        }

        if (targetCamera == null) return;

        float curY = targetCamera.transform.eulerAngles.y;
        float newY = Mathf.LerpAngle(curY, targetY, Time.deltaTime * smoothSpeed);
        Vector3 curPos = targetCamera.transform.position;
        Vector3 newPos = Vector3.Lerp(curPos, targetPosition, Time.deltaTime * smoothSpeed);

        targetCamera.transform.eulerAngles = new Vector3(
            targetCamera.transform.eulerAngles.x,
            newY,
            targetCamera.transform.eulerAngles.z
        );
        targetCamera.transform.position = newPos;
    }

    private void UpdateTargetsFromPayload(Payload payload)
    {
        // Nếu có center (continuous) -> map center.x về góc yaw
        if (payload.center != null && payload.center.Length >= 2)
        {
            float centerX = payload.center[0]; // normalized 0..1
            if (float.IsNaN(lastCenterX) || Mathf.Abs(centerX - lastCenterX) > 0.01f)
            {
                // Đảo chiều: centerX - 0.5 thay vì 0.5 - centerX
                float offsetDeg = (centerX - 0.5f) * yawRange * 2f;
                targetY = initialY + offsetDeg;
                lastCenterX = centerX;
            }
        }
        else
        {
            // fallback theo dir rời rạc (đã đảo: Left -> +, Right -> -)
            if (!string.IsNullOrEmpty(payload.dir))
            {
                if (payload.dir == "Left") targetY = initialY + yawRange * 0.4f;
                else if (payload.dir == "Right") targetY = initialY - yawRange * 0.4f;
                else targetY = initialY;
            }
        }

        // Tiến/lùi: ưu tiên depth hoặc dist
        if (payload.center != null && payload.depth > 0f)
        {
            float depth = payload.depth; // normalized difference mắt (lớn -> gần)
            float minDepth = 0.02f;
            float maxDepth = 0.12f;
            float depthFactor = Mathf.Clamp01((depth - minDepth) / (maxDepth - minDepth));
            targetPosition = initialPosition + initialForward * Mathf.Lerp(farOffset, closeOffset, depthFactor);
            lastDepth = depth;
        }
        else if (!string.IsNullOrEmpty(payload.dist))
        {
            if (payload.dist == "Close")
                targetPosition = initialPosition + initialForward * closeOffset;
            else if (payload.dist == "Far")
                targetPosition = initialPosition + initialForward * farOffset;
            else
                targetPosition = initialPosition;
        }
    }

    void ApplyHeadMovement(string direction, string distance)
    {
        if (targetCamera == null)
        {
            Debug.LogWarning("targetCamera not assigned on HeadTrackerReceiver");
            return;
        }

        Vector3 rotation = targetCamera.transform.eulerAngles;
        Vector3 position = targetCamera.transform.position;

        // --- Xoay trái/phải ---
        if (direction == "Left")
            rotation.y = Mathf.Lerp(rotation.y, rotation.y - 0.5f, 0.5f);
        else if (direction == "Right")
            rotation.y = Mathf.Lerp(rotation.y, rotation.y + 0.5f, 0.5f);

        // --- Tiến/lùi ---
        if (distance == "Close")
            position.z = Mathf.Lerp(position.z, position.z + 0.01f, 0.5f);
        else if (distance == "Far")
            position.z = Mathf.Lerp(position.z, position.z - 0.01f, 0.5f);

        targetCamera.transform.eulerAngles = rotation;
        targetCamera.transform.position = position;
    }

    private void OnApplicationQuit()
    {
        client.Close();
    }
}
