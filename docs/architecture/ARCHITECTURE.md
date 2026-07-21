# BÁO CÁO KIẾN TRÚC HỆ THỐNG CLUBREPORTHUB
**Môn học:** PRN232 — Microservices Architecture & Compliance  
**Tác giả:** Technical Software Architect & .NET 8 Microservices Specialist

---

## 1. ARCHITECTURE SUMMARY (TỔNG QUAN KIẾN TRÚC)

Dự án **ClubReportHub** được xây dựng theo kiến trúc **.NET 8 Microservices** nâng cao, vận hành hoàn toàn trong môi trường container hóa với **Docker Compose**. Hệ thống áp dụng nguyên tắc **Database-per-Service** với SQL Server 2022, sử dụng **YARP API Gateway** làm điểm truy cập duy nhất cho Client, kết hợp đồng thời giao tiếp đồng bộ **gRPC (HTTP/2)** cho tính toán hiệu năng cao và giao tiếp bất đồng bộ **Redis Streams** cho kiến trúc Event-Driven.

Kiến trúc tuân thủ nghiêm ngặt các nguyên lý thiết kế hệ thống phân tán:
1. **Single Entry Point**: Toàn bộ yêu cầu từ Web Frontend hoặc API Client đều đi qua YARP Gateway (Port 7000).
2. **Autonomous Microservices**: 7 REST Microservices quản lý các domain nghiệp vụ độc lập (Auth, Club, Activity, Report, Finance, Export, Notification).
3. **Dedicated gRPC Service**: `KpiGrpcService` là microservice gRPC độc lập, xử lý tính toán điểm KPI theo chuẩn Protobuf trên nền HTTP/2 unencrypted (h2c).
4. **Event-Driven Messaging**: Redis Streams `clubreporthub-events` truyền tải sự kiện nghiệp vụ giữa các Producer (Report, Club, Activity, Finance, Export) và Consumer (`NotificationService`) qua Consumer Group `notification-service` với cơ chế kiểm tra Idempotency tại Database (`ProcessedEvents`).
5. **Scheduled Background Processing**: Hangfire Server tích hợp trong `ReportService` chạy các tác vụ định kỳ kiểm tra hạn nộp báo cáo và phát sự kiện nhắc nhở.

---

## 2. VERIFIED COMPONENTS TABLE (BẢNG XÁC MINH CÁC THÀNH PHẦN HỆ THỐNG)

| Component Name | Type | Host Port | Internal Port | Protocol | Data Store | Primary Responsibility |
|---|---|---|---|---|---|---|
| **API Gateway** | YARP Reverse Proxy | 7000 | 8080 | HTTP/1.1 REST | N/A | Entry point duy nhất, reverse proxy, JWT Auth forwarding |
| **AuthService** | REST Microservice | 5101 | 8080 | HTTP/1.1 REST | `ClubReportHub_Auth` | Đăng nhập, đăng ký, cấp phát JWT Access/Refresh Token, quản lý Roles |
| **ClubService** | REST Microservice | 5102 | 8080 | HTTP/1.1 REST | `ClubReportHub_Club` | Quản lý thông tin CLB, thành viên, phân công Ban chủ nhiệm |
| **ReportService** | REST Microservice | 5103 | 8080 | HTTP/1.1 REST + gRPC Client | `ClubReportHub_Report` | Quản lý báo cáo, Workflow Draft -> Submit -> Approve, Hangfire Jobs, Redis Producer |
| **ExportService** | REST Microservice | 5104 | 8080 | HTTP/1.1 REST | `ClubReportHub_Export` | Xuất dữ liệu báo cáo ra file PDF / Excel |
| **NotificationService** | REST Microservice | 5105 | 8080 | HTTP/1.1 REST + Redis Consumer | `ClubReportHub_Notification` | Đọc Redis Stream qua Consumer Group, kiểm tra Idempotency, tạo Thông báo |
| **ActivityService** | REST Microservice | 5106 | 8080 | HTTP/1.1 REST | `ClubReportHub_Activity` | Quản lý danh sách hoạt động, sự kiện của các CLB |
| **FinanceService** | REST Microservice | 5107 | 8080 | HTTP/1.1 REST | `ClubReportHub_Finance` | Quản lý đề xuất ngân sách, kinh phí và quyết toán |
| **KpiGrpcService** | gRPC Microservice | 5110 *(Health)* | 8080 *(gRPC)*<br>8081 *(Health)* | HTTP/2 *(gRPC)*<br>HTTP/1.1 *(Health)* | Stateless *(N/A)* | Tính toán điểm KPI và xếp hạng CLB dựa trên dữ liệu Protobuf |
| **Redis 7 Alpine** | Event Stream Broker | 6379 | 6379 | Redis RESP / Streams | In-Memory Stream | Lưu trữ Stream `clubreporthub-events` phục vụ Event-Driven Pub/Sub |
| **SQL Server 2022** | RDBMS | 14333 | 1433 | T-SQL / TCP 1433 | 7 Logical Databases | Lưu trữ dữ liệu quan hệ độc lập theo từng Microservice |

---

## 3. GENERATED ARCHITECTURE ARTIFACTS (DANH SÁCH FILE TÀI LIỆU VÀ SƠ ĐỒ)

Tất cả các tài liệu và sơ đồ kiến trúc đã được khởi tạo thành công trong thư mục `docs/architecture/`:

1. **`docs/architecture/ClubReportHub-System-Architecture.mmd`**: Source sơ đồ kiến trúc hệ thống tổng thể theo chuẩn Mermaid.
2. **`docs/architecture/ClubReportHub-Redis-Sequence.mmd`**: Source sơ đồ trình tự (Sequence Diagram) cho luồng Redis Stream Report Submission.
3. **`docs/architecture/ClubReportHub-gRPC-Sequence.mmd`**: Source sơ đồ trình tự (Sequence Diagram) cho luồng REST → gRPC KPI Leaderboard.
4. **`docs/architecture/ClubReportHub-System-Architecture.drawio`**: File sơ đồ có thể mở và chỉnh sửa trực tiếp trên [diagrams.net (Draw.io)](https://app.diagrams.net).
5. **`docs/architecture/ClubReportHub-System-Architecture.svg`**: Ảnh Vector chất lượng cao phục vụ chèn vào tài liệu Word, Báo cáo đồ án hoặc Slide thuyết trình.
6. **`docs/architecture/ARCHITECTURE.md`**: Tài liệu thuyết minh chi tiết toàn bộ kiến trúc hệ thống.

---

## 4. MAIN DIAGRAM EXPLANATION (GIẢI THÍCH SƠ ĐỒ TỔNG THỂ)

Sơ đồ kiến trúc tổng thể được tổ chức theo 6 lớp (Layers) từ trên xuống dưới:

- **Layer 1 (Clients & Actors)**: Bao gồm Web Frontend (React/Vite), Postman, PowerShell CLI và các Actor hệ thống (`Admin`, `ClubManager`, `StudentAffairsAdmin`, `Treasurer`, `Member`).
- **Layer 2 (API Gateway)**: YARP Reverse Proxy lắng nghe tại Port Host `7000`. Tất cả request từ Client đều phải đi qua đây. Gateway thực hiện ủy quyền (Authorization Policy) và điều hướng (Route Forwarding) tới các container microservice tương ứng.
- **Layer 3A (REST Microservices)**: 7 microservices REST chạy trên các cổng riêng biệt (`5101` đến `5107`). Mỗi service đóng vai trò Bounded Context riêng.
- **Layer 3B (Isolated gRPC Service)**: `KpiGrpcService` lắng nghe tại Container Port `8080` (HTTP/2) để nhận kết nối gRPC nội bộ từ `ReportService`. Port Host `5110` được map với Port `8081` (HTTP/1.1) chỉ phục vụ kiểm tra Health Check từ bên ngoài Host.
- **Layer 4 (Async Messaging Infrastructure)**: Container Redis 7 Alpine đóng vai trò Message Broker quản lý Stream `clubreporthub-events`.
- **Layer 5 (Data Infrastructure)**: Container SQL Server 2022 chứa 7 Logical Databases độc lập. Không có bất kỳ service nào truy cập trực tiếp vào DB của service khác.
- **Layer 6 (Docker Compose Boundary)**: Đường viền bao quanh toàn bộ môi trường container hóa, đảm bảo các service giao tiếp qua tên service (DNS nội bộ Docker).

---

## 5. REDIS STREAM WORKFLOW EXPLANATION (LUỒNG REDIS STREAM BẤT ĐỒNG BỘ)

Các bước thực thi luồng nộp báo cáo và gửi thông báo bất đồng bộ (**Flow 1**):

1. **Step 1**: Club Manager gửi request `POST /api/reports/{id}/submit` kèm JWT Token tới API Gateway (Port 7000).
2. **Step 2**: Gateway chuyển tiếp request tới `ReportService` (Container Port 8080).
3. **Step 3**: `ReportService` kiểm tra điều kiện (số lượng chi tiết > 0, trạng thái Draft/Rejected), cập nhật trạng thái báo cáo thành `UnderReview` trong `ClubReportHub_Report` DB.
4. **Step 4**: `ReportService` gọi `RedisStreamEventBus.PublishAsync()`, phát một `ReportSubmittedEvent` vào Redis Stream `clubreporthub-events` bằng lệnh `XADD`.
5. **Step 5**: Redis trả về `RedisEntryId` (ví dụ: `1784615916727-0`). `ReportService` phản hồi HTTP 200 OK về Client.
6. **Step 6**: `NotificationService` chạy `BackgroundService` liên tục gọi `XREADGROUP` đọc message mới theo Consumer Group `notification-service`.
7. **Step 7**: `NotificationService` kiểm tra tính trùng lặp (Idempotency) bằng cách truy vấn bảng `ProcessedEvents` trong DB. Nếu chưa xử lý, tiến hành tạo thông báo mới.
8. **Step 8**: `NotificationService` lưu Notification và bản ghi `ProcessedEvent` vào `ClubReportHub_Notification` DB trong cùng một transaction.
9. **Step 9**: `NotificationService` gửi lệnh `XACK` về Redis Stream để xác nhận đã xử lý xong. Số lượng `Pending` giảm về 0.

---

## 6. gRPC WORKFLOW EXPLANATION (LUỒNG GIAO TIẾP gRPC ĐỒNG BỘ)

Các bước thực thi luồng tính điểm KPI qua gRPC (**Flow 2**):

1. **Step 1**: Administrator hoặc Client gửi request `GET /api/kpis/leaderboard?period=2026-Q3` tới API Gateway (Port 7000).
2. **Step 2**: Gateway route request tới `ReportService` (Port 5103).
3. **Step 3**: `ReportService` truy vấn `ClubReportHub_Report` DB để tổng hợp các chỉ số: Số báo cáo được duyệt (`approvedReports`), số hoạt động (`activityCount`), số sinh viên tham gia (`participantCount`), số báo cáo bị từ chối/trễ hạn (`rejectedReports`/`overdueReports`).
4. **Step 4**: Với mỗi CLB, `ReportService` khởi tạo một `CorrelationId` (GUID) và đóng gói request thành thông điệp Protobuf `KpiClubRequest`.
5. **Step 5**: `ReportService` (với vai trò gRPC Client) khởi tạo kênh HTTP/2 gọi phương thức `CalculateClubKpi` tới `http://kpi-grpc-service:8080`.
6. **Step 6**: `KpiGrpcService` nhận request qua HTTP/2, thực hiện thuật toán tính điểm:
   $$\text{Score} = (\text{Approved} \times 50) + (\text{Activities} \times 5) + (\text{Participants} \times 0.1) - (\text{Rejected} \times 10) - (\text{Overdue} \times 20)$$
   Đồng thời xác định Rating (`Excellent`, `Good`, `Average`, `Needs Improvement`).
7. **Step 7**: `KpiGrpcService` trả về thông điệp Protobuf `KpiClubResponse` qua kết nối HTTP/2. Log tại cả 2 service khớp `CorrelationId`.
8. **Step 8**: `ReportService` tổng hợp kết quả xếp hạng và trả về JSON `LeaderboardResponse` qua Gateway cho Client.

---

## 7. ASSUMPTIONS AND LIMITATIONS (CÁC GIỚI HẠN VÀ GIẢ ĐỊNH KỸ THUẬT)

1. **Giao thức gRPC Unencrypted (h2c)**: Do chạy trong mạng nội bộ Docker Compose an toàn giữa các container, kết nối gRPC giữa `ReportService` và `KpiGrpcService` sử dụng HTTP/2 không mã hóa TLS (h2c) thông qua cờ `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)`.
2. **Dual-Port Kestrel trên KpiGrpcService**: Để đáp ứng cả gọi gRPC HTTP/2 nội bộ và gọi `/health` HTTP/1.1 từ Host Windows, `KpiGrpcService` lắng nghe Port 8080 cho HTTP/2 và Port 8081 cho HTTP/1.1.
3. **At-Least-Once Delivery**: Redis Stream đảm bảo thông điệp được chuyển giao ít nhất một lần. Khả năng chống trùng lặp (Idempotency) được xử lý ở tầng ứng dụng tại `NotificationService` nhờ bảng `ProcessedEvents`.

---

## 8. SCRIPT HƯỚNG DẪN THUYẾT TRÌNH TRƯỚC GIẢNG VIÊN (PRESENTATION SCRIPT)

> *"Kính thưa Thầy/Cô, đây là Sơ đồ Kiến trúc Hệ thống của dự án ClubReportHub. Hệ thống được thiết kế theo đúng chuẩn Microservices với 6 lớp rõ ràng:*
>
> *Thứ nhất, tất cả yêu cầu từ Client đều đi qua điểm truy cập duy nhất là **YARP API Gateway** tại port 7000. Gateway chịu trách nhiệm Reverse Proxy và kiểm tra JWT Token.*
>
> *Thứ hai, tầng Microservices gồm 7 dịch vụ REST độc lập, áp dụng triệt để nguyên tắc **Database-per-Service** với 7 cơ sở dữ liệu riêng trên SQL Server. Không service nào truy cập trực tiếp DB của service khác.*
>
> *Thứ ba, hệ thống kết hợp 2 cơ chế giao tiếp nâng cao:*
> - *Giao tiếp bất đồng bộ qua **Redis Streams**: Khi báo cáo được Submit, `ReportService` phát sự kiện XADD. `NotificationService` dùng Consumer Group đọc sự kiện, kiểm tra trùng lặp qua bảng ProcessedEvents, tạo thông báo và gửi XACK.*
> - *Giao tiếp đồng bộ qua **gRPC HTTP/2**: Khi xem Bảng xếp hạng KPI, `ReportService` đóng vai trò gRPC Client gọi trực tiếp sang `KpiGrpcService` qua giao thức Protobuf trên nền HTTP/2 để tính điểm với độ trễ siêu thấp.*
>
> *Toàn bộ 11 containers được đóng gói và vận hành đồng bộ qua **Docker Compose**. Em xin cảm ơn Thầy/Cô."*

---

## 9. GIÁM SÁT TRẠNG THÁI GIT (GIT STATUS VERIFICATION)

Trạng thái Working Directory hiện tại (Các file kiến trúc mới khởi tạo nằm trong `docs/architecture/`):

```text
?? docs/architecture/ARCHITECTURE.md
?? docs/architecture/ClubReportHub-Redis-Sequence.mmd
?? docs/architecture/ClubReportHub-System-Architecture.drawio
?? docs/architecture/ClubReportHub-System-Architecture.mmd
?? docs/architecture/ClubReportHub-System-Architecture.svg
?? docs/architecture/ClubReportHub-gRPC-Sequence.mmd
```
*(Không có commit hoặc push được thực hiện).*
