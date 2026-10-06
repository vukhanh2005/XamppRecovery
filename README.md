# XAMPP MySQL Recovery Tool

Ứng dụng Windows desktop C# .NET 8 WinForms, ưu tiên giữ dữ liệu khi XAMPP báo `Error: MySQL shutdown unexpectedly`.

## Chạy ứng dụng

Tải **[bộ cài mới nhất](https://github.com/vukhanh2005/XamppRecovery/releases/latest)**, chọn `XamppMySqlRecoveryTool-Setup-1.0.0-x64.exe`. Chạy Setup, chọn thư mục cài và shortcut desktop nếu cần. Sau đó mở **XAMPP MySQL Recovery Tool** từ Start menu.

Bộ cài dành cho Windows 10/11 x64, cài theo tài khoản hiện tại tại `%LOCALAPPDATA%\Programs\XamppMySqlRecoveryTool`, không cần quyền Administrator. Gỡ bằng **Settings > Apps > Installed apps**; backup XAMPP và log phát sinh được giữ lại. Đóng ứng dụng và chờ phục hồi hoàn tất trước khi nâng cấp hoặc gỡ cài đặt. Bộ cài không cài kèm XAMPP.

Nếu không muốn cài, tải bản portable `XamppMySqlRecoveryTool.exe` trong cùng Release. Cả hai bản tự chứa .NET runtime. Bản phát hành chưa ký số; có thể đối chiếu SHA-256 với `SHA256SUMS.txt` trong Release bằng `Get-FileHash <file.exe> -Algorithm SHA256`.

1. Chọn thư mục XAMPP, mặc định `C:\xampp`.
2. Nhập tài khoản MySQL có quyền backup/shutdown; mặc định `root`, mật khẩu trống. Mật khẩu không lưu vào settings, log hay command line; chỉ truyền qua môi trường của tiến trình client con.
3. Dừng ứng dụng/web đang ghi database. Trong lúc phục hồi, không bấm Start MySQL ở XAMPP Control Panel và không để dịch vụ giám sát tự khởi động MySQL.
4. Bấm **Diagnose (read-only)** để xem port, process, Windows service, thư mục dữ liệu và log lỗi.
5. Bấm **Backups and Reset XAMPP**, đọc xác nhận rồi chọn **Continue**. **Cancel** là lựa chọn mặc định.
6. Chờ kết quả. **Open Backup Folder** mở thư mục backup; **Open XAMPP** mở Control Panel.

Trạng thái Running chỉ phản ánh tiến trình đã xác minh. Kết quả phục hồi thành công đòi hỏi đồng thời: đúng process còn sống, đúng port LISTENING và truy vấn MySQL có xác thực thành công.

## Quy tắc bảo vệ dữ liệu

- Không xóa database gốc. Bản gốc được đổi tên thành `mysql\data_broken_TIMESTAMP` trước khi kích hoạt bản sao.
- Không xóa backup cũ. Nếu trùng timestamp, tên thư mục có hậu tố riêng.
- Không dùng force-kill với `mysqld`. Chỉ yêu cầu dừng Windows service đúng PID hoặc dùng `mysqladmin shutdown`. Không xác minh được quyền sở hữu, không dừng được server, hoặc port thuộc process khác: dừng thao tác.
- Physical backup là bản sao toàn bộ `mysql\data`, gồm thư mục rỗng. So sánh danh sách thư mục/file, kích thước và SHA-256 từng file ở nguồn và đích khi MySQL đã dừng. Backup rỗng/thiếu/bị sửa không được dùng.
- Nếu server đang chạy và xác thực được: xuất `all-databases.sql` bằng `mysqldump`, gồm toàn bộ database người dùng, database hệ thống, tài khoản, routines, events và triggers. File SQL chứa nhiều database, không chia mỗi database thành một file riêng.
- Nếu dump toàn bộ bị lỗi, tool thử `user-databases.sql` cho database người dùng và phpMyAdmin, bỏ `mysql`/`sys`. Manifest ghi `SqlScope=USER_DATABASES`, tên file, SHA-256 và nguyên nhân dump toàn bộ thất bại. Backup SQL phạm vi này **không được dùng để rebuild InnoDB**; mọi bảng hệ thống vẫn nằm trong physical backup.
- Giữ `FLUSH TABLES WITH READ LOCK` qua lúc dump và shutdown để SQL snapshot không bỏ sót các ghi sau dump. Không lấy được khóa hoặc dump lỗi: ghi `SQL backup: FAILED`, chỉ tiếp tục nếu có thể dừng an toàn và backup vật lý đầy đủ. Server không chạy: `SQL backup: SKIPPED`.
- Chỉ sau `Physical backup: SUCCESS` mới chuẩn bị hoặc kích hoạt dữ liệu phục hồi. SQL thành công riêng lẻ không đủ để cho phép repair.
- Kiểm tra dung lượng trống tối thiểu ba lần kích thước data cộng 64 MiB. Cần thêm dung lượng cho SQL dump và các bản dữ liệu được giữ lại.
- Khóa độc quyền theo thư mục XAMPP chặn hai phiên tool cùng phục hồi một installation. Khóa này không ngăn ứng dụng khác tự khởi động MySQL.

SHA-256 xác nhận bản sao giống nguồn, không khẳng định dữ liệu nguồn không bị hỏng. Backup và SQL chứa dữ liệu, tài khoản nhạy cảm; bảo vệ quyền truy cập thư mục backup.

## Các mức phục hồi

### Level 1: xung đột port

Đọc bảng TCP IPv4/IPv6 bằng Windows IP Helper, lấy PID, tên process và các MySQL/MariaDB service qua WMI. Kiểm tra cả 3306 và port trong `my.ini`. Nếu port được process khác sử dụng, ghi hướng xử lý: dừng đúng dịch vụ bằng Windows Services hoặc điều chỉnh port server/client đồng bộ. Không sửa port hay kill process khác tự động.

### Level 2: khởi động lại bản sao đồng bộ

Tạo bản sao đã xác minh từ physical backup. Giữ nguyên `ibdata1`, `ib_logfile*`, `aria_log*` và các tablespace cùng nhau. Chỉ bỏ file `*.pid` trong bản sao, sau khi server đã dừng; bản gốc và backup giữ nguyên chúng.

Thử server trên port riêng, bind loopback, tắt event scheduler, replication autostart và binary log trong phiên kiểm tra. Khi đạt kiểm tra process/port/kết nối, dừng phiên thử rồi khởi động trên port cấu hình bình thường. Tool khởi động `mysqld --standalone`; không cài mới Windows service và không khôi phục trạng thái Running của dịch vụ đã dừng.

Nếu **lần khởi động vừa thử** báo `Can't open and lock privilege tables: Incorrect file format 'proxies_priv'`, tool phục hồi riêng index Aria của `mysql.proxies_priv` bằng `REPAIR TABLE ... USE_FRM`, dùng chính `.frm` và `.MAD` của bảng. Chỉ chạy trên candidate sau physical backup được xác minh. Chế độ bootstrap tắt mạng và InnoDB, không tạo server bỏ xác thực trên port TCP. Không thay bảng tài khoản, không đặt lại mật khẩu, không lấy bảng từ template.

Sau bước này, SHA-256 phải xác nhận các file database người dùng, bảng tài khoản khác và InnoDB không đổi. `aria_chk` và `CHECK TABLE ... EXTENDED` cho `proxies_priv` phải đạt; server phải đăng nhập được bằng tài khoản gốc và tạo SQL backup cho dữ liệu người dùng trước khi commit. Nếu các điều kiện này thất bại, rollback. Đây là sửa index của một bảng Aria đã xác định, không phải rebuild InnoDB; không cần SQL dump từ trước khi server hỏng.

Nếu một bảng hệ thống khác bị hỏng nhưng MySQL đã khởi động được và SQL backup của database người dùng thành công, tool giữ nguyên bảng đó và hiển thị **MySQL started with warnings**. Đây chỉ là phục hồi khả năng khởi động, không khẳng định tất cả bảng đều lành. Không tự chạy `aria_chk --recover` trên các bảng quyền khác: lệnh có thể trả exit code 0 nhưng làm mất bản ghi bị lỗi checksum. Khôi phục các quyền này cần nguồn backup lành hoặc xem xét riêng; không xóa quyền để làm thông báo lỗi biến mất.

Với `.frm` được tạo bằng MariaDB cũ, server có thể cập nhật trường creator-version 4 byte. Tool chỉ chấp nhận trường này đổi đúng sang phiên bản `mysqld.exe` đang chạy; mọi byte định nghĩa bảng còn lại phải giống ban đầu.

### Level 3: rebuild có điều kiện

Chỉ thực hiện nếu Level 2 thất bại **và có SQL snapshot đầy đủ, nhất quán của phiên backup hiện tại**. Kiểm tra hash SQL trước khi dùng.

`mysql\backup` phải tồn tại, có database `mysql` và không chứa thư mục database người dùng. Giữ trọn bộ file engine của template; không ghép `ibdata1` cũ với `.ibd` hoặc dictionary mới. Khởi động template trên loopback/port riêng, dùng `root` trống theo template XAMPP, import toàn bộ SQL, reload quyền, xác minh lại bằng tài khoản gốc và so sánh danh sách database/table/view. Database `mysql`, `performance_schema`, `phpmyadmin`, `sys` được phân biệt với database người dùng; SQL vẫn giữ các database hệ thống có dữ liệu cần thiết.

Nếu MySQL hỏng đến mức không thể tạo SQL snapshot, tool vẫn bắt buộc physical backup nhưng **không tự rebuild InnoDB**. Ghép thư mục database và xóa redo/Aria log có thể làm mất dữ liệu. Tool rollback, giữ mọi bản sao để xử lý chuyên sâu trên bản clone.

`ponytail:` không tự chạy `innodb_force_recovery`, sửa trang InnoDB, import tablespace rời hoặc nâng cấp phiên bản engine. Chỉ bổ sung các phương án này khi có kiểm chứng riêng theo engine/version và nguồn phục hồi độc lập.

## Rollback và mất điện

Kế hoạch lưu trước từng bước tại `mysql\recovery-plan.json`; bản lưu lịch sử ở `xampp_mysql_backups\plans`. JSON được ghi vào file tạm, flush xuống đĩa, rồi thay thế file kế hoạch.

Khi lỗi sau khi kích hoạt candidate: dừng MySQL an toàn, chuyển candidate sang `data_failed_TIMESTAMP`, đưa `data_broken_TIMESTAMP` về `data`. Nếu bản gốc không còn, dùng physical backup đã xác minh. MySQL giữ trạng thái dừng sau rollback để kiểm tra.

Nếu ứng dụng bị ngắt giữa hai bước đổi tên, lần bấm Recovery/Restore tiếp theo sẽ xử lý giao dịch chưa hoàn tất trước khi bắt đầu thao tác mới. Chỉ mở ứng dụng không tự thay đổi dữ liệu.

Nếu hệ điều hành từ chối rename/ghi file hoặc server không chịu shutdown, rollback không thể hoàn thành ngay. Tool ghi **ROLLBACK PENDING**, giữ nguyên original/backup/candidate và journal; không force-kill để che lỗi. Giải quyết shutdown/quyền truy cập rồi chạy lại. Không tự xóa `data_broken_*`, `data_failed_*`, `data_recovery_*` hoặc sửa journal.

## Restore Previous Backup

1. Chọn thư mục timestamp có `manifest.json` trong `xampp_mysql_backups` của XAMPP đang chọn.
2. Xác nhận restore. Tool kiểm tra backup được chọn trước, rồi backup toàn bộ trạng thái hiện tại.
3. Sau khi backup hiện tại được xác minh, tool mới đổi dữ liệu và thử khởi động; lỗi sẽ quay về trạng thái trước restore.

Restore vật lý yêu cầu cùng đường dẫn XAMPP, cùng hash `mysqld.exe` và cùng nội dung `my.ini`. Không tự restore chéo phiên bản hoặc tự ghi đè cấu hình. Tài khoản nhập trong UI phải truy cập được cả trạng thái hiện tại và backup được chọn; nếu mật khẩu đã đổi, cần xử lý thủ công trên clone trước. Không dùng restore như công cụ migration.

## Phạm vi hỗ trợ và quyền

- Windows x64, XAMPP dùng `mysql\bin\mysqld.exe`, `mysql.exe`, `mysqladmin.exe`, `mysqldump.exe`, cấu hình `mysql\bin\my.ini` hoặc `mysql\my.ini`, dữ liệu nằm trong `mysql\data`.
- Xác minh process yêu cầu đường dẫn exe và `--defaults-file` tuyệt đối trỏ đúng cấu hình. Command line không đọc được hoặc dùng đường dẫn tương đối: yêu cầu người dùng dừng đúng instance, có thể cần Administrator.
- Từ chối junction/symlink/reparse point, `.isl`, cấu hình storage ngoài data, cấu hình include, replication/cluster, plugin tự nạp, init SQL, binary/relay log tùy chỉnh, `skip-networking`, `skip-grant-tables`, `innodb_force_recovery` khác 0. Layout tablespace ngoài thư mục, mã hóa/plugin đặc thù và storage engine tùy chỉnh cần DBA kiểm tra; không dùng tool cho các layout này.
- Backup bảo toàn nội dung và cấu trúc file; không phải bản sao ACL, alternate data streams hay ảnh toàn bộ ổ đĩa. Bản gốc vẫn được giữ bằng rename.
- Chỉ nâng quyền khi thiếu quyền đọc process, dừng dịch vụ, đọc/ghi/rename thư mục. Nút **Run as administrator** khởi động lại qua UAC, không truyền mật khẩu MySQL sang phiên mới.
- UI không cho đóng khi thao tác đang chạy. Có thể Cancel trước khi bắt đầu; không ngắt giữa một giao dịch thay đổi database.
- Server kiểm tra dùng port loopback riêng để tránh client bình thường truy cập nhầm. Sau khi server trên port chính khởi động, ứng dụng khác có thể kết nối: giữ client dừng đến khi tool thông báo hoàn tất.

## File đầu ra

```text
XamppRecoveryTool.sln
XamppRecoveryTool/                WinForms và các service
XamppRecoveryTool.Checks/         Kiểm thử console, không thêm framework
build.bat                        Build Release và kiểm thử an toàn
publish.bat                      Build, test, publish EXE tự chứa runtime
setup.bat                        Publish và tạo bộ cài Windows bằng Inno Setup
installer/XamppRecovery.iss       Cấu hình bộ cài, shortcut và uninstall
release/XamppMySqlRecoveryTool.exe

<XAMPP>/xampp_mysql_backups/yyyy-MM-dd_HH-mm-ss/
  data/                          Physical backup
  all-databases.sql              Khi SQL backup thực hiện được
  user-databases.sql             Fallback khi chỉ dump được dữ liệu người dùng
  manifest.json                  Inventory, hash, SQL status, Complete
  my.ini.snapshot                Cấu hình tại thời điểm backup
<XAMPP>/mysql/recovery-plan.json
<EXE directory>/logs/recovery_yyyy-MM-dd_HH-mm-ss_GUID.log
```

Nếu thư mục EXE không cho ghi, log chuyển sang `%LOCALAPPDATA%\XamppMySqlRecoveryTool\logs`. Đường dẫn thực tế luôn được in trong UI. Backup chưa hoàn tất giữ `Complete=false`, không được restore.

## Build và kiểm thử

Cài .NET SDK 8 hoặc mới hơn trên Windows; lần build/publish đầu cần Internet hoặc NuGet cache cho targeting/runtime packs .NET 8. Không dùng thư viện NuGet ngoài runtime .NET.

```bat
build.bat
publish.bat
```

Để tạo bộ cài, cài [Inno Setup](https://jrsoftware.org/isinfo.php) 6.2 trở lên rồi chạy `setup.bat`. Nếu compiler nằm ở đường dẫn riêng, đặt `ISCC` thành đường dẫn đầy đủ tới `ISCC.exe`. Kết quả ở `release\XamppMySqlRecoveryTool-Setup-1.0.0-x64.exe`. Không đưa binary, database, SQL dump hoặc log vào Git; binary được đính kèm GitHub Release.

Kiểm thử cơ bản tạo fixture riêng trong `test-artifacts`, kiểm tra port IPv4/IPv6, ownership, đường dẫn nguy hiểm, backup SHA-256, backup rỗng/hỏng, khóa phiên phục hồi, rollback và crash journal. Không chạy repair trên XAMPP thật.

Kiểm thử tích hợp tùy chọn:

```bat
dotnet run --project XamppRecoveryTool.Checks -c Release -- --integration C:\xampp
```

Chỉ **đọc/copy** `mysql\bin`, `mysql\share`, `mysql\lib` từ đường dẫn cung cấp. Tạo database mới với `mysql_install_db.exe` trong fixture riêng, dùng port loopback riêng, kiểm tra dump/recovery/restore/rebuild với bảng InnoDB mẫu. Không copy hay sửa `C:\xampp\mysql\data`. Fixture, log và `ui-preview.png` được giữ để kiểm tra. Dừng nếu binary không hỗ trợ bootstrap này.

Các test không chứng minh mọi kiểu corruption hoặc mọi phiên bản MySQL đều phục hồi được. Mục tiêu là kiểm chứng đường đi thành công, từ chối thao tác thiếu điều kiện, và khả năng trả lại dữ liệu gốc khi lỗi.
