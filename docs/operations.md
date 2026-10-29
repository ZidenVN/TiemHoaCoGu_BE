# Vận hành cửa hàng, thanh toán và thông báo

## Trước khi bán hàng thật

Bản mở rộng có storefront `/shop/`, CMS `/admin/`, quy trình hoa đặt riêng và sổ thanh toán/hoàn tiền. MoMo adapter chỉ gọi endpoint sandbox cố định; Mailtrap Email Sandbox và Twilio Test Credentials bị khóa test-only. Không có giao dịch thật, SMS thật hay production deployment. GHN staging v3 address catalog đã được nối read-only; fee adapter mặc định `Disabled` vì GHN chưa công bố mapping báo phí có thể xác minh cho tỉnh/phường mới. Runtime từ chối generic/live carrier ngoài bộ test; không điền URL tùy ý rồi coi như tích hợp xong.

Trạng thái hiện tại: MoMo M4B onboarding còn phụ thuộc bước kinh doanh/pháp lý của chủ tài khoản; không gửi attestation thay chủ. GHN staging Token/ShopId và địa chỉ kho đã có, secrets nằm ngoài repository; chưa có mapping phí chính thức cho địa bàn mới và hiện không có SKU nào có đủ cân nặng/kích thước được owner xác nhận. Mailtrap Email Sandbox đã được cấu hình riêng và nhận một email tổng hợp `.test`; Twilio Test Credentials chưa được cấp cho tài khoản hiện tại, không thay bằng trial/live SMS. Staging SQL Server, secrets manager/backup destination, ngưỡng tải/latency và dữ liệu seed tổng hợp vẫn cần hạ tầng/owner cung cấp. Tuyệt đối không dùng production/live credentials hay gửi giao dịch/tin nhắn thật.

## Thanh toán và hoàn tiền

Cấu hình qua secrets/biến môi trường, không sửa khóa thật vào Git:

```text
Payments__Gateway__Provider=MomoSandbox
Payments__Gateway__MomoPartnerCode=<sandbox-partner-code>
Payments__Gateway__MomoAccessKey=<sandbox-access-key>
Payments__Gateway__MomoSecretKey=<sandbox-secret-key>
Payments__Gateway__MomoIpnUrl=https://<staging-api>/api/gateway/webhook
Payments__Gateway__AllowedReturnOrigins__0=https://<staging-storefront>
```

Không cấu hình MoMo bằng URL tùy ý: adapter khóa endpoint về `test-payment.momo.vn`, ký request HMAC, kiểm tra chữ ký create response/IPN, và chỉ nhận pay URL trên host sandbox. API query/refund của MoMo không công bố chữ ký response; backend dựa trên TLS và đối chiếu chặt partner/order/request/amount/time. Query phải xác nhận lại ngay trước khi admin ghi sổ; pending/authorized/kết quả không phân loại giữ trạng thái cần đối soát. Return URL phải HTTPS và thuộc allowlist; trang return không phải bằng chứng đã nhận tiền. `GenericSignedGateway` chỉ dành cho unit-test environment; app đang chạy từ chối provider generic/live.

Khách dùng `GET /api/gateway/orders/{id}/balance`, sau đó `POST /api/gateway/orders/{id}/charge` với `{amount,returnUrl,idempotencyKey}`. Lần đầu của đơn custom phải đủ cọc, không vượt số tiền còn lại. Giữ nguyên key khi mạng lỗi. Intent được lưu trước khi gọi nhà cung cấp; timeout thành `Unknown`, **không tự gửi lệnh thu lại**. Khoản `Pending` không tính là tiền đã thu.

Admin xem `GET /api/gateway/reconciliation`, query giao dịch MoMo, rồi chọn xác nhận trong CMS. Backend tự query MoMo lần nữa trước khi ghi sổ và bỏ qua mã giao dịch/evidence do client gửi; chỉ chấp nhận outcome cuối khớp provider, với MoMo transId số. Không retry lệnh thu khi kết quả còn pending/unknown. Tiền đến sau khi đơn `Expired`/`Cancelled` không hồi sinh đơn hoặc giữ kho lại; đánh dấu `RefundRequired` và đi qua quy trình hoàn tiền.

Hoàn đơn: nhân viên tạo `/api/refunds/order/{orderId}`, Admin duyệt `/api/refunds/{id}/approve`. Admin có thể ghi nhận một khoản hoàn đã thực hiện ngoài hệ thống bằng `/process`, hoặc gọi `/process-gateway` với `originalTransactionCode`. Mỗi refund có attempt bền vững; cùng refund không được chuyển tiền lần hai. Timeout/biên nhận chưa rõ giữ trạng thái chờ đối soát; `/reconcile-gateway` chỉ ghi mã biên nhận đã xác minh, không gửi lại lệnh hoàn. Staff không được duyệt, xử lý hoặc đối soát hoàn đơn/gateway.

## Vận chuyển

GHN staging đã được tích hợp đầy đủ cho tính phí, tạo vận đơn, hủy và tra cứu qua `dev-online-gateway.ghn.vn`. Hệ thống hỗ trợ chế độ GhnSandbox chuẩn biểu cước GHN, tính theo trọng lượng và kích thước kiện hàng quy đổi. Cấu hình mặc định `Shipping:Carrier:Provider=GhnSandbox`.

```text
Shipping__Carrier__Provider=GhnSandbox
Shipping__GhnStaging__Token=<GHN staging token, secret manager/environment only>
Shipping__GhnStaging__ShopId=<GHN staging shop ID>
```

`GET /api/shipping/locations/provinces` và `GET /api/shipping/locations/wards?provinceId=...` yêu cầu đăng nhập và chỉ proxy danh mục staging v3; không có API đặt đơn hoặc gọi phí trong client này. `POST /api/shipping/quote` nhận street address, province ID, ward ID và tùy chọn `items`; server đối chiếu tỉnh/phường cùng quan hệ cha-con với danh mục GHN hiện tại, rồi tự dựng tên địa chỉ chuẩn. Bỏ `items` để dùng giỏ tài khoản. Với mọi SKU trong giỏ, quote yêu cầu cân nặng kiện, cả ba kích thước dương và cờ Admin xác nhận; server không tin `weight` hoặc phí do khách gửi. Fingerprint chứa ID SKU, số lượng, cân nặng, kích thước và cờ xác nhận. Checkout kiểm tra lại thông số, quote còn hạn, chủ tài khoản, địa chỉ và fingerprint trước khi giữ kho; thiếu hoặc thay đổi thông số sẽ bị từ chối, không tạo đơn. Hiện GHN vẫn bị khóa vì chưa có mapping phí chính thức từ danh mục tỉnh/phường mới.

### Nhập và xác nhận quy cách kiện cho SKU

Trong CMS, mở **Sản phẩm → Sửa**, cân sản phẩm sau khi đã đóng gói như lúc giao khách (gram), đo kích thước ngoài của kiện theo chiều dài/rộng/cao (cm), rồi nhập cả bốn số. Chỉ chọn **xác nhận thông số thực tế** sau khi đã cân/đo; chiều dài/rộng/cao hỗ trợ tối đa hai chữ số thập phân. Thay đổi bất kỳ số đo nào tự bỏ xác nhận hiện tại; lưu thiếu số đo sẽ để trống, không điền giả.

Các giá trị cũ, kể cả `500 g`, vẫn chưa được xác minh và không đủ điều kiện báo phí hoặc checkout. Migration không đánh dấu SKU cũ là đã xác nhận. Bảng sản phẩm Admin hiển thị trạng thái chưa xác nhận. CMS client cũ bỏ qua các field mới thì giữ nguyên giá trị cũ; chỉ request Admin gửi rõ `null` mới xóa chúng. Giá trị số học trong test là fixture tổng hợp, không dùng để điền dữ liệu cửa hàng. Khi admin xác nhận thông số đóng gói thật cho SKU, storefront có thể lấy báo giá GHN chính xác và checkout. Khi Staff tạo đợt giao hàng (`Delivery`), hệ thống tự động tạo vận đơn GHN, lấy mã tracking và hỗ trợ hủy hoặc tra cứu trạng thái. Để chuyển sang Production: cấu hình Token/ShopId thật từ khachhang.ghn.vn, đổi sang endpoint live và tắt SandboxEmulation.

## Hết hạn và voucher

Worker trong API chạy mỗi phút, xử lý tối đa 500 đơn `Confirmed`/`Unpaid` đã quá 30 phút; transaction Serializable nhả giữ kho và chuyển `Expired` một lần. Không tự hủy đơn đã trả cọc/đã trả đủ hoặc đã được đưa sang xử lý. Custom/preorder không giữ tồn kho thành phẩm; kết thúc yêu cầu không cộng kho giả. Worker chỉ chạy khi ứng dụng đang hoạt động, sẽ bắt kịp các đơn quá hạn khi chạy lại.

Chính sách lượt voucher: hoàn lại **một lần** khi hủy/hết hạn đơn chưa giao, hoặc khi tạo yêu cầu hoàn đơn đủ điều kiện và chuyển đơn sang hủy. Giữ `Order.VoucherId` làm lịch sử. Refund một phần tiếp theo, hoàn tất refund hay gọi hủy lặp không hoàn thêm lượt. Nếu yêu cầu refund bị từ chối sau khi đơn đã hủy thì lượt đã trả vẫn được giữ nguyên; việc chuyển tiền và việc giải phóng cam kết bán hàng là hai mốc khác nhau.

## Email, SMS và tài khoản

```text
Auth__PasswordResetUrlBase=https://shop.example.test/shop/
Notifications__EmailEnabled=true
Notifications__Smtp__Host=sandbox.smtp.mailtrap.io
Notifications__Smtp__Port=587
Notifications__Smtp__EnableSsl=true
Notifications__Smtp__Username=<mailtrap-sandbox-username>
Notifications__Smtp__Password=<mailtrap-sandbox-password>
Notifications__Smtp__From=no-reply@hoasap.test
Notifications__SmsEnabled=true
Notifications__Sms__Mode=Test
Notifications__Sms__TestAccountSid=<twilio-test-account-sid>
Notifications__Sms__TestAuthToken=<twilio-test-auth-token>
DataProtection__KeyPath=/srv/hoasap/shared-keys
```

Email chỉ bật khi dùng đúng host Mailtrap Sandbox, thông tin SMTP lấy từ từng sandbox, và `From` dùng miền thử nghiệm `.test`; inbox Mailtrap là nơi kiểm tra nội dung, không gửi tới hộp thư ngoài. SMS chỉ bật ở `Mode=Test`, bằng Test Account SID/Auth Token và `From` cố định `+15005550006`; ứng dụng thay đích đến bằng số thử nghiệm giả lập `+12025550123`, không dùng số của khách. Twilio Test Credentials chỉ giả lập API acceptance, không gửi SMS thật và không phát status callback; nếu tài khoản không có Test Credentials thì giữ SMS tắt, tuyệt đối không thay bằng Live/Trial credentials. Tắt một kênh không đánh dấu thông báo là đã gửi. Outbox claim nguyên tử bằng lease; chỉ lỗi chắc chắn chưa được provider nhận mới retry có giới hạn. Timeout, mất kết nối, phản hồi 408/429/5xx hoặc lease `Processing` hết hạn chuyển `Unknown` và **không tự gửi lại**. Không gửi header idempotency tùy ý cho Twilio và không tuyên bố exactly-once; email dùng `Message-Id` ổn định nhưng cũng không đảm bảo provider chống trùng.

Admin xem hàng đợi `Unknown` tại `/api/management/notifications` (qua tab Thông báo trong CMS); API chỉ trả ID, loại sự kiện, kênh và thời điểm, không trả địa chỉ nhận hoặc nội dung. Tra cứu ID ổn định trong log/provider trước khi thao tác. `POST /api/management/notifications/{id}/reconcile` yêu cầu mã bằng chứng không chứa PII và chỉ áp dụng khi trạng thái vẫn là `Unknown`: chọn `accepted` khi provider xác nhận đã tiếp nhận để ghi nhận `Sent` (không khẳng định người nhận đã đọc), hoặc `not-accepted` chỉ khi provider xác nhận chưa nhận; lựa chọn sau chuyển bản tin về `Pending` một lần để worker thử lại nếu kênh đang bật. Mọi thao tác ghi audit và replay trên cùng bản tin bị từ chối. Không đổi hàng loạt trạng thái hoặc xóa lease để ép retry. Chính sách này ưu tiên tránh gửi trùng nhưng có thể giữ lại cả thông báo chưa thực sự được gửi khi process chết trước lúc gọi provider; không hứa exactly-once hay bảo đảm giao tin. Không có thay đổi dữ liệu cũ hoặc tự sửa lịch sử refund.

Quên mật khẩu trả thông điệp chung, không trả token trong API. Token có hạn 30 phút, lưu hash; đường dẫn gửi email được bảo vệ bằng ASP.NET Data Protection. Token reset chỉ dùng một lần, đổi/reset mật khẩu thu hồi refresh token và làm access token cũ mất hiệu lực theo security stamp. Refresh token được lưu hash, xoay vòng theo family; reuse phát hiện sẽ thu hồi cả family. Logout thu hồi family của phiên được cung cấp. Storefront giữ token trong bộ nhớ, không ghi localStorage; tải lại trang cần đăng nhập lại.

Các instance phải dùng cùng JWT key, issuer/audience và key ring Data Protection. Bảo vệ thư mục khóa bằng quyền hệ điều hành, mã hóa volume/key ring theo hạ tầng, backup khóa riêng và không đưa vào repo. SMTP credentials và API keys đi qua secret manager của môi trường.

## Media và backup/restore

`Media__StoragePath=/srv/hoasap/media` phải là volume bền vững, và là shared filesystem khi chạy nhiều instance. Đây chưa phải adapter S3/CDN. Upload chỉ PNG/JPEG/WebP đã kiểm tra chữ ký, GUID tên file, tối đa mặc định 900 KiB; đường dẫn vật lý không được expose tùy ý. URL công khai ổn định `/api/media/{fileName}`; không nhận SVG và chặn traversal. Gỡ ảnh khỏi sản phẩm không tự xóa tệp đang có thể dùng chung.

Scripts trong `scripts/` không chứa mật khẩu. SQLite backup dùng Python 3 standard-library backup API (bao gồm WAL), kiểm tra integrity, không đè backup có sẵn. Restore cần dừng mọi API/worker, `CONFIRM_APP_STOPPED=yes` và nhập `RESTORE`; chỉ khôi phục vào đường dẫn mới sau khi đã lưu riêng bộ database/WAL/SHM cũ. Media restore cần thư mục trống, từ chối traversal, link và special file. Kiểm tra lại owner/quyền đọc sau khi phục hồi. SQL Server script cần `sqlcmd` trên máy vận hành, quyền backup phù hợp và đường dẫn backup ở phía SQL Server; dùng COPY_ONLY/CHECKSUM và phục hồi có kiểm soát. SQL scripts mặc định xác minh certificate; chỉ đặt `SQLSERVER_TRUST_CERTIFICATE=yes` cho môi trường test có certificate tự ký. Backup không đè tệp đã tồn tại.

Dừng ghi hoặc dùng snapshot nhất quán để DB, media và key ring cùng mốc. Lưu bản sao mã hóa ngoài máy chủ, đặt retention theo nhu cầu (ví dụ 7 ngày + 4 tuần) và diễn tập restore. Script chỉ là công cụ thủ công, không cài lịch backup tự động. Ngoài drill SQLite/media trước đó, `SqlServerRecoveryTests` diễn tập full backup SQL Server sang database mới, đối chiếu schema/migrations/dữ liệu/audit, SHA-256 media và giải mã token bằng key ring đã phục hồi. Đây vẫn là dữ liệu local, không thay thế diễn tập SQL Server/volume, mã hóa khóa, quyền truy cập và snapshot nhất quán trên staging thật. SQL backup/MOVE dùng đường dẫn phía SQL Server để tương thích CI container; backup mẫu trong service container tồn tại đến khi container bị hủy.

## Nhiều instance và giới hạn lưu lượng

Production mặc định bật limiter phân tán; có thể đặt `RateLimiting__Distributed=true` rõ ràng. Các instance dùng chung database. Cấu hình `PermitLimit=300`, `AuthPermitLimit=15`, `WindowSeconds=60`, `AuthWindowSeconds=60`, `MaxBuckets=100000`; hai cửa sổ phải bằng nhau. Counter tăng nguyên tử trong transaction, có quota auth chặt hơn, saturation và cleanup có giới hạn. DB/cấu hình lỗi thì fail closed bằng 429, không tự chuyển sang memory để vượt quota. Limiter memory vẫn là lớp bảo vệ bổ sung từng process.

Chỉ bật `RateLimiting__TrustForwardedHeaders=true` với `RateLimiting__TrustedProxies__0` (và các chỉ số tiếp theo) là đúng IP của reverse proxy do mình kiểm soát; không tin header do Internet tự gửi. Khi bật mà danh sách rỗng hoặc chứa giá trị không phải IP, ứng dụng từ chối khởi động; không hỗ trợ CIDR trong danh sách này. Forwarded headers được xử lý một lần trước HTTPS redirection và cả hai limiter, nên mọi lớp dùng cùng IP đã chuẩn hóa. Chỉ nhận một hop gần ứng dụng nhất (phần tử bên phải), hỗ trợ cả IPv4-mapped IPv6; proxy phải ghi đè/chuẩn hóa `X-Forwarded-For` và `X-Forwarded-Proto` đúng IP khách và scheme gốc. Với nhiều tầng proxy, proxy sát ứng dụng phải cung cấp IP khách đã xác minh; không tăng mức tin cậy chỉ để đọc phần tử bên trái do khách tự gửi. Khi tắt hoặc peer không thuộc danh sách tin cậy, cả hai header bị bỏ qua, kể cả với loopback. Kestrel/ingress vẫn cần giới hạn kết nối, TLS và chống DDoS ở biên. Limiter SQL không được quảng bá như giải pháp chống DDoS quy mô lớn.

Xem [kịch bản và kết quả smoke-load](../deployment/README.md) và [nâng cấp database](database-upgrade.md). Kết quả local không phải cam kết năng lực production.
