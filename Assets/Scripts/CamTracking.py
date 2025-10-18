import cv2
import mediapipe as mp
import socket, json

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
unity_address = ("127.0.0.1", 5005)

mp_face_mesh = mp.solutions.face_mesh
cap = cv2.VideoCapture(0)

prev_depth_ratio = None
alpha = 0.6             # smoothing factor (0..1), tăng -> ít nhiễu hơn nhưng trễ
auto_min = 1.0
auto_max = 0.0
auto_window = 300       # số frame để cập nhật min/max trước khi reset (tinh chỉnh)
frame_count = 0
# ...existing code...
with mp_face_mesh.FaceMesh(refine_landmarks=True) as face_mesh:
    while True:
        ret, frame = cap.read()
        if not ret:
            break
        frame = cv2.rotate(frame, cv2.ROTATE_90_CLOCKWISE)
        frame = cv2.flip(frame, 1) 
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        results = face_mesh.process(rgb)

        if results.multi_face_landmarks:
            # dùng face đầu tiên (max_num_faces=1 nên chỉ có 1)
                face = results.multi_face_landmarks[0]

                mp.solutions.drawing_utils.draw_landmarks(
                    frame,
                    face,
                    mp_face_mesh.FACEMESH_TESSELATION,
                    landmark_drawing_spec=None,
                    connection_drawing_spec=mp.solutions.drawing_styles.get_default_face_mesh_tesselation_style()
                )

                h, w, _ = frame.shape

                # một số điểm quan trọng (kinh nghiệm: 1 ~ mũi, 33 ~ mắt trái, 263 ~ mắt phải)
                nose = face.landmark[1]
                left_eye = face.landmark[33]
                right_eye = face.landmark[263]

                # ...changed code...
                # compute interocular distance in pixels and normalize by estimated face width
                lx_px = left_eye.x * w
                ly_px = left_eye.y * h
                rx_px = right_eye.x * w
                ry_px = right_eye.y * h
                interocular_px = ((lx_px - rx_px)**2 + (ly_px - ry_px)**2) ** 0.5

                # estimate face width from landmarks x-range
                xs = [lm.x for lm in face.landmark]
                face_width_px = (max(xs) - min(xs)) * w
                if face_width_px <= 1e-6:
                    continue

                depth_ratio = interocular_px / (face_width_px + 1e-6)  # ~ 0..1, lớn -> gần

                # compute normalized face center (use eyes midpoint) and direction
                center_x = (left_eye.x + right_eye.x) / 2.0
                center_y = (left_eye.y + right_eye.y) / 2.0

                # simple left/center/right thresholds (tune as needed)
                if center_x < 0.45:
                    direction = "Left"
                elif center_x > 0.55:
                    direction = "Right"
                else:
                    direction = "Center"

                # update auto min/max (simple running min/max)
                frame_count += 1
                if depth_ratio < auto_min: auto_min = depth_ratio
                if depth_ratio > auto_max: auto_max = depth_ratio
                if frame_count >= auto_window:
                    # clamp reasonable bounds to avoid degenerate ranges
                    auto_min = min(auto_min, 0.6)
                    auto_min = max(auto_min, 0.02)
                    auto_max = max(auto_max, 0.03)
                    auto_max = min(auto_max, 0.8)
                    frame_count = 0

                # smoothing
                if prev_depth_ratio is None:
                    smooth_ratio = depth_ratio
                else:
                    smooth_ratio = alpha * depth_ratio + (1.0 - alpha) * prev_depth_ratio
                prev_depth_ratio = smooth_ratio

                # normalize relative to observed min/max
                span = max(auto_max - auto_min, 1e-6)
                norm = (smooth_ratio - auto_min) / span
                norm = max(0.0, min(1.0, norm))

                # thresholds (tùy chỉnh): >0.66 = Close, <0.33 = Far, else Normal
                if norm > 0.66:
                    distance = "Close"
                elif norm < 0.33:
                    distance = "Far"
                else:
                    distance = "Normal"

                # gửi JSON về Unity qua UDP
                payload = {
                    "dir": direction,
                    "dist": distance,
                    "center": [center_x, center_y],
                    "depth": smooth_ratio
                }

                try:
                    sock.sendto(json.dumps(payload).encode('utf-8'), unity_address)
                except Exception as e:
                    # nếu muốn log lỗi gửi: print(e)
                    pass

                cv2.putText(frame, f"Dir: {direction}, Dist: {distance}", (10, 30),
                            cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 255, 0), 2)

        cv2.imshow("Head Tracking", frame)
        if cv2.waitKey(1) == 27:
            break


    cap.release()
    cv2.destroyAllWindows()
    sock.close()