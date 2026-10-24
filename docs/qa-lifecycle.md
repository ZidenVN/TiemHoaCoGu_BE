# Kết quả kiểm tra bản mở rộng

## Bổ sung acceptance sau main `a260845`

**Backend 192 đạt, 0 lỗi, 0 bỏ qua**, bao gồm **12 test tích hợp SQL Server thật**; frontend **6/6 đạt**. Release build thành công; lúc biên dịch test còn warning CS1998 có sẵn trong helper `ApiFixture.AuthenticatedAsync`, không có lỗi build. Cổng `scripts/verify-test-results.py` xác nhận TRX không có test bỏ qua và có đủ các suite SQL; CI gọi cổng này sau `dotnet test` để không nhận kết quả xanh giả do thiếu SQL.

- Staff tạo yêu cầu hoàn workshop không còn ghi nhầm `ApprovedBy`: `Requested` để trống người duyệt, DTO trả null; audit vẫn ghi đúng Staff là người tạo. Test gọi đúng API Staff, kiểm tra Staff bị cấm approve/process/process-gateway/reconcile-gateway, rồi Admin duyệt mới ghi đúng id Admin. Không tự sửa lịch sử refund cũ.
- Timeout/mất kết nối/kết quả gửi SMS-email không xác định hoặc lease `Processing` hết hạn chuyển `Unknown`, không tự gửi lại; lỗi chắc chắn chưa nhận vẫn retry có backoff. Message id giữ ổn định. Test không gọi nhà cung cấp thật. Thông báo chưa rõ kết quả có thể bị giữ chờ đối soát thay vì được tự giao lại; xem [runbook](operations.md).
- Thêm SQL test tranh ghế cuối bằng context/service độc lập, replay booking và hủy lặp/audit. Thử thêm qua **hai process API Release thật**: **5/5 vòng đạt**, mỗi vòng đúng một `200` và một `409`, replay cùng id, nhả ghế đúng một lần. Lượt đầu phát hiện lỗi EF bọc SQL deadlock thành 500; middleware đã nhận diện inner exception để trả 409, không tự chạy lại transaction. [Kết quả hai process](../deployment/results/workshop-race.json).
- Drill full backup/restore SQL Server vào DB mới đạt: schema/migrations, dữ liệu mẫu và audit giữ nguyên; media khớp SHA-256; token giải mã được bằng key ring đã phục hồi. SQL dùng đường dẫn phía server để hỗ trợ CI container. Đây **không phải** drill snapshot/volume/khóa mã hóa trên staging thật.

Các số liệu trong phần dưới là acceptance lịch sử. Branch sandbox follow-up bên dưới có lượt kiểm tra mới riêng; không tạo production credentials, không gọi provider thật, không deploy và không merge PR.

## Sandbox follow-up — kiểm tra local

Đã thêm MoMo sandbox payment/refund/query/IPN; Admin reconciliation tự query MoMo lần nữa trước khi ghi sổ và bỏ qua mã/evidence từ client. Twilio test transport bỏ URL/token khôi phục khỏi SMS body; Mailtrap chỉ bật đúng Email Sandbox; notification `Unknown` có Admin reconciliation/audit. Carrier mặc định `Disabled` vì GHN adapter và address mapping sau thay đổi địa giới 2025 chưa được xác minh.

Lượt mới nhất: `dotnet test HoaSap.sln -c Release` đạt **205 passed, 12 skipped, 0 failed**. 12 test SQL Server không chạy vì lượt này không có `HOASAP_TEST_SQLSERVER`; chúng không được coi là đạt. Frontend lint, typecheck, **32/32 unit tests**, Prettier và production build đạt; preview E2E **30/30** đạt trong lượt trước. Mọi provider HTTP trong test bị fake handler chặn; chưa có MoMo/Mailtrap/Twilio credentials và chưa gọi API provider.

Đây mới là bằng chứng local. GHN staging quote/create/cancel/tracking, SQL Server/volume/key-ring snapshot restore trên staging, migration/rollback rehearsal và k6/load acceptance với ngưỡng được team duyệt vẫn chờ hạ tầng, credentials, fixture và quyết định của owner. Các artifacts local trong `deployment/results/` không phải chứng nhận staging.

## Kết quả chính

**166 test đạt, 0 lỗi, 0 bỏ qua**, gồm **9 test SQL Server 2022 thật**. Build Release toàn solution đạt **0 warning, 0 error**. EF xác nhận model không còn thay đổi chưa có migration. Kiểm tra cú pháp JavaScript, Bash và `git diff --check` đều đạt.

Mốc trước đợt mở rộng là 107 test. Các test mới bao phủ báo giá/revision/duyệt/cọc/tiến độ custom, preorder không tạo kho giả, gateway intent và callback, refund attempt/đối soát, hết hạn đơn/khôi phục voucher, shipping quote gắn giỏ, refresh/reset token, outbox/lease, media, limiter và nâng cấp SQLite có dữ liệu. Có kiểm tra Staff không được gọi các nghiệp vụ tài chính gateway mới; reset hoặc đổi quyền Admin thu hồi phiên cũ; client CMS cũ không làm trọng lượng SKU quay về mặc định.

```bash
dotnet build HoaSap.sln -c Release
# HOASAP_TEST_SQLSERVER lấy từ secrets, chỉ trỏ SQL Server dùng thử có quyền tạo/xóa DB.
dotnet test HoaSap.sln -c Release --no-build
```

Không đặt `HOASAP_TEST_SQLSERVER` thì 9 test SQL được đánh dấu bỏ qua. Kết quả 166/166 nói trên đã có biến này và chạy trên instance local riêng, không truy cập dữ liệu thật.

## Dữ liệu cũ và phục hồi

Fixture SQLite được sinh từ schema commit `13d90e6`, chứa tài khoản, đơn, khoản tiền, tồn kho đang giữ và voucher đã dùng. Test nâng cấp kiểm tra dữ liệu cũ giữ nguyên, thêm các bảng/cột mới và chạy lại không thay đổi dữ liệu. Các case schema không tương thích, backup bắt buộc và rollback cũng được kiểm tra. SQL Server kiểm tra migration từ schema EF trước đó và nghiệp vụ transaction bằng database GUID riêng.

Drill backup/restore SQLite/media bằng dữ liệu mẫu đạt integrity check, đối chiếu hàng của Customers/Orders/Payments/Inventories/Vouchers và SHA-256 media. SQL Server restore/volume của hạ tầng triển khai **chưa được diễn tập**. Xem [kết quả drill](../deployment/results/backup-restore.json).

## Hai instance và giao diện

Smoke-load chạy hai process Release dùng chung SQL Server: **1.200/1.200 request trả 200**, concurrency 20, p95 **47,91 ms**, p99 **63,66 ms**. Kiểm tra qua hai process xác nhận auth/giỏ, checkout replay cùng id, hủy lặp không trả kho hai lần. Cặp limiter quota 5 trả đúng 5 thành công rồi 3 lần 429. Đây là tải ngắn cục bộ, không phải chứng nhận năng lực production. [Số liệu đầy đủ](../deployment/results/local-smoke.json).

Đã mở trình duyệt kiểm tra storefront `/shop/` tải catalog/workshop, đăng nhập và hiện tài khoản; CMS `/admin/` đăng nhập và tải danh sách workshop/các thao tác ca học. Đây là kiểm tra trình duyệt thủ công có giới hạn, không phải bộ E2E browser đầy đủ. Kịch bản k6 đã được bổ sung nhưng chưa chạy bài tải kéo dài.

## Cần làm trước khi bán thật

Chưa thu/hoàn tiền hoặc gửi email/SMS thật. MoMo có adapter sandbox nhưng chưa có credentials hay provider smoke test; Mailtrap/Twilio test mode cũng chưa được cấu hình. GHN chưa tích hợp và carrier đang bị disable. Cần cấu hình staging tách biệt, secrets sandbox, địa chỉ kho/ID mapping GHN và cân trọng lượng/kích thước thực tế từng SKU; sau đó mới chạy end-to-end, restore rehearsal, migration rollback và load tests theo ngưỡng owner duyệt. Kết quả local không khẳng định CI đã xanh hoặc hệ thống sẵn sàng staging/production. Xem [runbook](operations.md).
