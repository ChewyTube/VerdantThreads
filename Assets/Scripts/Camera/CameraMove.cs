using UnityEngine;

public class CameraMove : MonoBehaviour
{
    [Header("移动设置")]
    [SerializeField] private float moveSpeed = 10f;
    [SerializeField] private float acceleration = 50f;
    [SerializeField] private float deceleration = 80f;

    [Header("视角设置")]
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private float minPitch = -89f;
    [SerializeField] private float maxPitch = 89f;

    private Vector3 currentVelocity;
    private float pitch;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        pitch = transform.eulerAngles.x;
        if (pitch > 180f) pitch -= 360f;
    }

    void Update()
    {
        HandleRotation();
        HandleMovement();
    }

    private void HandleRotation()
    {
        float yaw = Input.GetAxis("Mouse X") * mouseSensitivity;
        transform.Rotate(Vector3.up, yaw, Space.World);

        pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        transform.localEulerAngles = new Vector3(pitch, transform.eulerAngles.y, 0f);
    }

    private void HandleMovement()
    {
        float verticalInput = 0f;
        if (Input.GetKey(KeyCode.Space)) verticalInput += 1f;
        if (Input.GetKey(KeyCode.LeftShift)) verticalInput -= 1f;

        Vector3 inputDir = new Vector3(
            Input.GetAxisRaw("Horizontal"),
            verticalInput,       
            Input.GetAxisRaw("Vertical")
        ).normalized;

        Vector3 targetVelocity = (RemoveYPortion(transform.forward).normalized * inputDir.z
                                + RemoveYPortion(transform.right).normalized * inputDir.x
                                + Vector3.up * inputDir.y) * moveSpeed;

        float smoothRate = inputDir.sqrMagnitude > 0.01f ? acceleration : deceleration;
        currentVelocity = Vector3.MoveTowards(currentVelocity, targetVelocity, smoothRate * Time.deltaTime);

        transform.position += currentVelocity * Time.deltaTime;
    }

    private Vector3 RemoveYPortion(Vector3 v)
    {
        return new(v.x, 0, v.z);
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}