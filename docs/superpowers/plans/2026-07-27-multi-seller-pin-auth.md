# Мультипродавец на одной кассе: смена + PIN — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** За одной кассой работают 2–5 продавцов; переключение между ними стоит два тапа, работает без сети, и каждый чек получает корректный `seller_id`.

**Architecture:** Три уровня личности — устройство (`Cash-Authorization`), смена (email+пароль, JWT живёт до закрытия смены), продавец (PIN, проверяется локально по кэшированному хэшу). Бэкенд отдаёт кассе ростер продавцов с хэшами PIN и правами; клиент кэширует его в SQLite и подставляет `seller_id` в тело документа; сервер валидирует продавца по `cash_users`.

**Tech Stack:** Go 1.x + gin + pgx (бэкенд, `C:\work\cloudmarket-server`); .NET 10 + Avalonia + CommunityToolkit.Mvvm + Microsoft.Data.Sqlite + xUnit (клиент, `C:\work\vv-cash`).

**Спека:** [`docs/superpowers/specs/2026-07-27-multi-seller-pin-auth-design.md`](../specs/2026-07-27-multi-seller-pin-auth-design.md)

**Порядок:** Фаза 1 (бэкенд) целиком до Фазы 2 — клиент зависит от контракта `GET /cashes/seller/`. Фаза 1 обратно совместима: старые сборки кассы продолжают работать, потому что `seller` в теле документа опционален.

---

## Команды проверки

**Бэкенд** (из `C:\work\cloudmarket-server`):
```bash
go test ./users/ ./cashes/ ./documents/ -v
```

**Клиент** (из `C:\work\vv-cash`, PowerShell):
```bash
dotnet test tests/VvCash.Tests/VvCash.Tests.csproj
```

Если приложение запущено, сборка падает на файловой блокировке — собирать в отдельную папку: `dotnet build -o build/verify`.

---

## Структура файлов

### Бэкенд — создаётся

| Файл | Ответственность |
|---|---|
| `migrations/20260727000000_seller_pin.up.sql` / `.down.sql` | `users.pin_hash`, `users.pin_updated_at`, `cash_users.max_discount`, `document_expenses.approved_by_id` |
| `migrations/20260727000100_shift_close_permission.up.sql` / `.down.sql` | permission-строка `cashes.shift_close` |
| `users/pin.go` | хэширование/проверка/валидация PIN (PBKDF2) |
| `users/pin_test.go` | тесты PIN-хелперов |
| `users/pin_controller.go` | `POST /users/pin/`, `POST /users/pin/reset/` |

### Бэкенд — правится

| Файл | Что меняется |
|---|---|
| `permcode/codes.go` | новый код `CashShiftClose` |
| `cashes/cash_repo.go` | `SellerItem` += `PinHash/CanRefund/CanCloseShift/MaxDiscount`; расширенный SQL в `GetSellersForCash`; `UpsertCashUser` += `maxDiscount` |
| `cashes/cash_controller.go` | `AssignUser` принимает `max_discount` |
| `documents/document_expense_repo.go` | новый `IsCashSellerAllowed`; `InsertDocumentExpense` += `approvedBy` |
| `documents/document_interfaces.go` | сигнатуры выше |
| `documents/document_expense_service.go` | выбор `seller_id` из тела + подмена при невалидном продавце |
| `documents/serializers.go` | `DocumentExpenseSerializer` += `ApprovedBy` |
| `users/user_repo.go`, `users/user_service.go`, `users/routes.go` | запись PIN |

### Клиент — создаётся

| Файл | Ответственность |
|---|---|
| `src/VvCash/Models/SellerInfo.cs` | продавец + его capability-флаги |
| `src/VvCash/Services/PinHasher.cs` | проверка PBKDF2-хэша, только verify |
| `src/VvCash/Services/ISellerSession.cs` / `SellerSession.cs` | текущий продавец, переключение, блокировка, таймаут |
| `src/VvCash/Services/Api/ISellerRosterService.cs` / `SellerRosterService.cs` | загрузка `/cashes/seller/` + кэш |
| `src/VvCash/ViewModels/SellerSwitchViewModel.cs` | состояние оверлея выбора/подтверждения |
| `src/VvCash/Views/SellerSwitchView.axaml` / `.axaml.cs` | плитки + PIN-пад |
| `tests/VvCash.Tests/PinHasherTest.cs` | |
| `tests/VvCash.Tests/SellerSessionTest.cs` | |
| `tests/VvCash.Tests/SellerRosterServiceTest.cs` | |
| `tests/VvCash.Tests/SellerSwitchViewModelTest.cs` | |
| `tests/VvCash.Tests/DocumentRequestSellerTest.cs` | |

### Клиент — правится

| Файл | Что меняется |
|---|---|
| `src/VvCash/Services/Data/IOfflineStorageService.cs` / `OfflineStorageService.cs` | таблица `Sellers` + CRUD |
| `src/VvCash/App.axaml.cs` | регистрация новых сервисов в DI |
| `src/VvCash/ViewModels/PosViewModel.cs` | чип продавца, гейт при первом товаре, `SellerId` в `DocumentRequest` |
| `src/VvCash/Views/PosView.axaml` | чип + подключение оверлея |
| `src/VvCash/Assets/i18n/*.json` | новые строки (5 языков) |

Логика переключения **не** уходит в `PosViewModel` — он уже 1139 строк.

---

# ФАЗА 1 — Бэкенд

## Task 1: Миграции схемы

**Files:**
- Create: `migrations/20260727000000_seller_pin.up.sql`
- Create: `migrations/20260727000000_seller_pin.down.sql`
- Create: `migrations/20260727000100_shift_close_permission.up.sql`
- Create: `migrations/20260727000100_shift_close_permission.down.sql`

- [ ] **Step 1: Написать up-миграцию схемы**

`migrations/20260727000000_seller_pin.up.sql`:
```sql
ALTER TABLE users ADD COLUMN IF NOT EXISTS pin_hash text;
ALTER TABLE users ADD COLUMN IF NOT EXISTS pin_updated_at timestamptz;
ALTER TABLE cash_users ADD COLUMN IF NOT EXISTS max_discount numeric;
ALTER TABLE document_expenses ADD COLUMN IF NOT EXISTS approved_by_id uuid REFERENCES users(id);
```

- [ ] **Step 2: Написать down-миграцию**

`migrations/20260727000000_seller_pin.down.sql`:
```sql
ALTER TABLE document_expenses DROP COLUMN IF EXISTS approved_by_id;
ALTER TABLE cash_users DROP COLUMN IF EXISTS max_discount;
ALTER TABLE users DROP COLUMN IF EXISTS pin_updated_at;
ALTER TABLE users DROP COLUMN IF EXISTS pin_hash;
```

- [ ] **Step 3: Написать миграцию permission-строки**

Формат скопирован с `migrations/20260726000100_promotion_permissions.up.sql`.

`migrations/20260727000100_shift_close_permission.up.sql`:
```sql
insert into permissions(readable_name, handler) select 'cashes.shift_close', 'cashes.shift_close' where not exists (select 1 from permissions where handler = 'cashes.shift_close');
```

`migrations/20260727000100_shift_close_permission.down.sql`:
```sql
delete from permissions where handler = 'cashes.shift_close';
```

- [ ] **Step 4: Проверить, что миграции применяются**

Run: `go build ./...`
Expected: сборка проходит (миграции встроены через `migrations_embed.go`, синтаксис проверяется применением на тестовой БД в интеграционных тестах Task 4).

- [ ] **Step 5: Commit**

```bash
git add migrations/20260727000000_seller_pin.up.sql migrations/20260727000000_seller_pin.down.sql migrations/20260727000100_shift_close_permission.up.sql migrations/20260727000100_shift_close_permission.down.sql
git commit -m "feat: add seller PIN, per-seller discount cap and approved_by columns"
```

---

## Task 2: PIN-хелперы (хэш, проверка, валидация формата)

**Files:**
- Create: `users/pin.go`
- Test: `users/pin_test.go`

- [ ] **Step 1: Написать падающие тесты**

`users/pin_test.go`:
```go
package users

import "testing"

func TestHashPINRoundTrip(t *testing.T) {
	encoded, err := HashPIN("4821")
	if err != nil {
		t.Fatalf("HashPIN returned error: %v", err)
	}
	if !VerifyPIN("4821", encoded) {
		t.Error("VerifyPIN rejected the correct PIN")
	}
	if VerifyPIN("4822", encoded) {
		t.Error("VerifyPIN accepted a wrong PIN")
	}
}

func TestHashPINUsesRandomSalt(t *testing.T) {
	a, _ := HashPIN("4821")
	b, _ := HashPIN("4821")
	if a == b {
		t.Error("two hashes of the same PIN are identical; salt is not random")
	}
}

func TestVerifyPINRejectsMalformed(t *testing.T) {
	for _, encoded := range []string{"", "nonsense", "pbkdf2_sha256$abc$x$y", "argon2$1$2$3"} {
		if VerifyPIN("4821", encoded) {
			t.Errorf("VerifyPIN accepted malformed hash %q", encoded)
		}
	}
}

func TestValidatePINFormat(t *testing.T) {
	valid := []string{"4821", "90210", "718293"}
	for _, pin := range valid {
		if err := ValidatePINFormat(pin); err != nil {
			t.Errorf("ValidatePINFormat(%q) = %v, want nil", pin, err)
		}
	}

	invalid := []string{"123", "1234567", "12a4", "", "1111", "0000", "1234", "4321", "3456"}
	for _, pin := range invalid {
		if err := ValidatePINFormat(pin); err == nil {
			t.Errorf("ValidatePINFormat(%q) = nil, want error", pin)
		}
	}
}
```

- [ ] **Step 2: Запустить тесты — убедиться что падают**

Run: `go test ./users/ -run 'TestHashPIN|TestVerifyPIN|TestValidatePIN' -v`
Expected: FAIL, `undefined: HashPIN`

- [ ] **Step 3: Реализовать хелперы**

`users/pin.go`:
```go
package users

import (
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"fmt"
	"strconv"
	"strings"

	"golang.org/x/crypto/pbkdf2"
)

// PIN hashing uses PBKDF2-HMAC-SHA256 rather than argon2 because the cash register
// verifies PINs offline in C#, where argon2 needs a third-party package. Both
// standard libraries ship PBKDF2, which removes the cross-language mismatch risk.
const (
	pinIterations = 100000
	pinKeyLen     = 32
	pinSaltLen    = 16
	pinAlgo       = "pbkdf2_sha256"
)

// ErrPINFormat is returned when a PIN does not satisfy the format rules.
var ErrPINFormat = errors.New("pin must be 4-6 digits and not a trivial sequence")

// HashPIN returns an encoded hash in the form pbkdf2_sha256$iter$b64salt$b64hash.
func HashPIN(pin string) (string, error) {
	salt := make([]byte, pinSaltLen)
	if _, err := rand.Read(salt); err != nil {
		return "", err
	}
	key := pbkdf2.Key([]byte(pin), salt, pinIterations, pinKeyLen, sha256.New)
	return fmt.Sprintf("%s$%d$%s$%s", pinAlgo, pinIterations,
		base64.StdEncoding.EncodeToString(salt),
		base64.StdEncoding.EncodeToString(key)), nil
}

// VerifyPIN reports whether pin matches the encoded hash.
func VerifyPIN(pin, encoded string) bool {
	parts := strings.Split(encoded, "$")
	if len(parts) != 4 || parts[0] != pinAlgo {
		return false
	}
	iter, err := strconv.Atoi(parts[1])
	if err != nil || iter <= 0 {
		return false
	}
	salt, err := base64.StdEncoding.DecodeString(parts[2])
	if err != nil {
		return false
	}
	want, err := base64.StdEncoding.DecodeString(parts[3])
	if err != nil || len(want) == 0 {
		return false
	}
	got := pbkdf2.Key([]byte(pin), salt, iter, len(want), sha256.New)
	return subtle.ConstantTimeCompare(got, want) == 1
}

// ValidatePINFormat enforces 4-6 digits and rejects trivial sequences.
func ValidatePINFormat(pin string) error {
	if len(pin) < 4 || len(pin) > 6 {
		return ErrPINFormat
	}
	for i := 0; i < len(pin); i++ {
		if pin[i] < '0' || pin[i] > '9' {
			return ErrPINFormat
		}
	}
	if isTrivialPIN(pin) {
		return ErrPINFormat
	}
	return nil
}

// isTrivialPIN reports repeated digits (1111) and runs of consecutive digits (1234, 4321).
func isTrivialPIN(pin string) bool {
	same, asc, desc := true, true, true
	for i := 1; i < len(pin); i++ {
		if pin[i] != pin[0] {
			same = false
		}
		if pin[i] != pin[i-1]+1 {
			asc = false
		}
		if pin[i] != pin[i-1]-1 {
			desc = false
		}
	}
	return same || asc || desc
}
```

- [ ] **Step 4: Запустить тесты — убедиться что проходят**

Run: `go test ./users/ -run 'TestHashPIN|TestVerifyPIN|TestValidatePIN' -v`
Expected: PASS (4 теста)

- [ ] **Step 5: Commit**

```bash
git add users/pin.go users/pin_test.go
git commit -m "feat: add PBKDF2 PIN hashing and format validation"
```

---

## Task 3: Эндпоинт установки PIN

**Files:**
- Create: `users/pin_controller.go`
- Modify: `users/user_repo.go` (добавить метод), `users/user_interfaces.go`, `users/routes.go`
- Modify: `permcode/codes.go`

- [ ] **Step 1: Добавить permission-код**

В `permcode/codes.go`, в блок `// ── cashes (...)`, после `CashCreate`:
```go
	CashShiftClose = "cashes.shift_close" // право закрывать смену на кассе
	UserPINReset   = "users.pin_reset"    // POST /users/pin/reset/
```

- [ ] **Step 2: Написать репозиторный метод записи PIN**

В `users/user_repo.go` добавить:
```go
// SetUserPIN stores an encoded PIN hash for the user and stamps the update time.
func (r *UserRepo) SetUserPIN(ctx context.Context, db base.PGXDB, userID, encodedHash string) error {
	_, err := db.Exec(ctx,
		`UPDATE users SET pin_hash = $2, pin_updated_at = NOW() WHERE id = $1`,
		userID, encodedHash,
	)
	return err
}
```

Добавить сигнатуру в интерфейс репозитория в `users/user_interfaces.go`:
```go
	SetUserPIN(ctx context.Context, db base.PGXDB, userID, encodedHash string) error
```

- [ ] **Step 3: Написать контроллер**

`users/pin_controller.go`:
```go
package users

import (
	"net/http"

	"cloudmarket/lctx"
	"cloudmarket/response"

	"github.com/gin-gonic/gin"
)

type pinInput struct {
	PIN string `json:"pin" binding:"required"`
}

// SetOwnPIN handles POST /users/pin/ — the caller sets their own cash-register PIN.
func (ctrl *UserController) SetOwnPIN(c *gin.Context) {
	in := &pinInput{}
	if err := c.BindJSON(in); err != nil {
		c.JSON(http.StatusBadRequest, response.ErrorFromError(err))
		return
	}
	if err := ValidatePINFormat(in.PIN); err != nil {
		c.JSON(http.StatusBadRequest, response.ErrorFromString(err.Error()))
		return
	}

	userID, err := lctx.GetUser(c)
	if err != nil {
		c.JSON(http.StatusInternalServerError, response.ErrorAndLog(err, "SetOwnPIN"))
		return
	}

	ctrl.storePIN(c, userID, in.PIN)
}

type pinResetInput struct {
	UserID string `json:"user" binding:"required"`
	PIN    string `json:"pin" binding:"required"`
}

// ResetPIN handles POST /users/pin/reset/ — an administrator sets someone else's PIN.
func (ctrl *UserController) ResetPIN(c *gin.Context) {
	in := &pinResetInput{}
	if err := c.BindJSON(in); err != nil {
		c.JSON(http.StatusBadRequest, response.ErrorFromError(err))
		return
	}
	if err := ValidatePINFormat(in.PIN); err != nil {
		c.JSON(http.StatusBadRequest, response.ErrorFromString(err.Error()))
		return
	}

	ctrl.storePIN(c, in.UserID, in.PIN)
}

func (ctrl *UserController) storePIN(c *gin.Context, userID, pin string) {
	encoded, err := HashPIN(pin)
	if err != nil {
		c.JSON(http.StatusInternalServerError, response.ErrorAndLog(err, "storePIN"))
		return
	}

	pool, err := lctx.Pool(c)
	if err != nil {
		return
	}

	if err := ctrl.service.SetUserPIN(c.Request.Context(), pool, userID, encoded); err != nil {
		c.JSON(http.StatusInternalServerError, response.ErrorAndLog(err, "storePIN"))
		return
	}

	c.JSON(http.StatusOK, response.Correct())
}
```

**Важно:** имя импорта пакета контекста (`lctx`) и структура `UserController`/`ctrl.service` должны совпадать с тем, что уже используется в `users/user_controller.go` — сверить импорты в этом файле и повторить их один в один.

- [ ] **Step 4: Пробросить метод через сервис**

В `users/user_service.go`:
```go
// SetUserPIN stores the encoded PIN hash for a user.
func (s *UserService) SetUserPIN(ctx context.Context, pool *pgxpool.Pool, userID, encodedHash string) error {
	return s.repo.SetUserPIN(ctx, pool, userID, encodedHash)
}
```

Добавить сигнатуру в сервисный интерфейс в `users/user_interfaces.go`.

- [ ] **Step 5: Зарегистрировать роуты**

В `users/routes.go`, в группу аутентифицированных пользовательских роутов:
```go
	users.POST("/pin/", ctrl.SetOwnPIN)
	authz.POST(users, "/pin/reset/", permcode.UserPINReset, ctrl.ResetPIN)
```

`SetOwnPIN` намеренно без `authz` — пользователь меняет свой собственный PIN, отдельное право тут излишне. Если файл ещё не импортирует `authz`/`permcode`, добавить импорты.

- [ ] **Step 6: Собрать**

Run: `go build ./... && go vet ./users/`
Expected: без ошибок

- [ ] **Step 7: Commit**

```bash
git add users/pin_controller.go users/user_repo.go users/user_service.go users/user_interfaces.go users/routes.go permcode/codes.go
git commit -m "feat: add endpoints for setting and resetting cash-register PIN"
```

---

## Task 4: Расширение `GET /cashes/seller/`

**Files:**
- Modify: `cashes/cash_repo.go:57-65` (`SellerItem`), `cashes/cash_repo.go:241-263` (`GetSellersForCash`)
- Test: `cashes/seller_roster_test.go` (создать)

- [ ] **Step 1: Написать падающий тест на форму ответа**

`cashes/seller_roster_test.go`:
```go
package cashes

import (
	"encoding/json"
	"testing"
)

func TestSellerItemSerializesRosterFields(t *testing.T) {
	item := SellerItem{
		ID:            "u-1",
		FirstName:     "Азиз",
		LastName:      "Каримов",
		Email:         "aziz@example.com",
		IsSeller:      true,
		CanSell:       true,
		PinHash:       "pbkdf2_sha256$100000$c2FsdA==$aGFzaA==",
		CanRefund:     true,
		CanCloseShift: false,
		MaxDiscount:   15,
	}

	raw, err := json.Marshal(item)
	if err != nil {
		t.Fatalf("marshal failed: %v", err)
	}

	var got map[string]any
	if err := json.Unmarshal(raw, &got); err != nil {
		t.Fatalf("unmarshal failed: %v", err)
	}

	for _, key := range []string{"pin_hash", "can_refund", "can_close_shift", "max_discount"} {
		if _, ok := got[key]; !ok {
			t.Errorf("serialized seller is missing key %q", key)
		}
	}
	if got["can_refund"] != true {
		t.Errorf("can_refund = %v, want true", got["can_refund"])
	}
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `go test ./cashes/ -run TestSellerItemSerializes -v`
Expected: FAIL, `unknown field PinHash in struct literal`

- [ ] **Step 3: Расширить структуру**

В `cashes/cash_repo.go` заменить `SellerItem` (строки 57–65) на:
```go
type SellerItem struct {
	ID        string `json:"id"`
	FirstName string `json:"first_name"`
	LastName  string `json:"last_name"`
	Email     string `json:"email"`
	Phone     string `json:"phone"`
	IsSeller  bool   `json:"is_seller"`
	CanSell   bool   `json:"can_sell"`

	// Roster fields consumed by the cash register for offline seller switching.
	PinHash       string  `json:"pin_hash"`
	CanRefund     bool    `json:"can_refund"`
	CanCloseShift bool    `json:"can_close_shift"`
	MaxDiscount   float64 `json:"max_discount"`
}
```

- [ ] **Step 4: Расширить запрос**

В `cashes/cash_repo.go` заменить тело `GetSellersForCash` на:
```go
// ЧЕРНОВИК — НЕ КОПИРОВАТЬ. Две ошибки, найденные при реализации:
//   1. `u.phone` — такой колонки в `users` нет; из-за неё GetSellersForCash падал
//      и на master (эндпоинт всегда отдавал 500). Починено отдельным коммитом.
//   2. join на `stores.max_discount` — такой колонки не существует, см. примечание
//      «РЕШЕНО при реализации» ниже.
// Фактическая реализация — в `cashes/cash_repo.go` на ветке feat/seller-pin:
// EXISTS-подзапросы с UNION по прямым и групповым правам, по образцу
// `authorization.AuthRepo.EffectivePermissionCodes`.
func (r *CashRepo) GetSellersForCash(ctx context.Context, db base.PGXDB, cashID string) ([]SellerItem, error) {
	rows, err := db.Query(ctx,
		`SELECT cu.user_id, u.first_name, u.last_name, u.email, u.phone,
		        cu.is_seller, cu.can_sell,
		        COALESCE(u.pin_hash, ''),
		        EXISTS (
		          SELECT 1 FROM permissions p
		          LEFT JOIN user_permissions up ON up.permission_id = p.id AND up.user_id = cu.user_id
		          LEFT JOIN group_permissions gp ON gp.permission_id = p.id
		          LEFT JOIN user_groups ug ON ug.group_id = gp.group_id AND ug.user_id = cu.user_id
		          WHERE p.handler = 'documents.MakeReturn'
		            AND (up.user_id IS NOT NULL OR ug.user_id IS NOT NULL)
		        ) AS can_refund,
		        EXISTS (
		          SELECT 1 FROM permissions p
		          LEFT JOIN user_permissions up ON up.permission_id = p.id AND up.user_id = cu.user_id
		          LEFT JOIN group_permissions gp ON gp.permission_id = p.id
		          LEFT JOIN user_groups ug ON ug.group_id = gp.group_id AND ug.user_id = cu.user_id
		          WHERE p.handler = 'cashes.shift_close'
		            AND (up.user_id IS NOT NULL OR ug.user_id IS NOT NULL)
		        ) AS can_close_shift,
		        COALESCE(cu.max_discount, s.max_discount, 0) AS max_discount
		 FROM cash_users cu
		 JOIN users u ON u.id = cu.user_id
		 JOIN cashes c ON c.id = cu.cash_id
		 JOIN warehouses w ON w.id = c.warehouse_id
		 LEFT JOIN stores s ON s.id = w.store_id
		 WHERE cu.cash_id = $1 AND cu.can_sell = true`,
		cashID,
	)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var result []SellerItem
	for rows.Next() {
		var s SellerItem
		if err := rows.Scan(&s.ID, &s.FirstName, &s.LastName, &s.Email, &s.Phone,
			&s.IsSeller, &s.CanSell,
			&s.PinHash, &s.CanRefund, &s.CanCloseShift, &s.MaxDiscount); err != nil {
			return nil, err
		}
		result = append(result, s)
	}
	return result, rows.Err()
}
```

**РЕШЕНО при реализации:** колонки `stores.max_discount` не существует. `stores.GetMaxAllowedDiscount` (`stores/settings.go`) читает EAV-таблицу `store_settings` по ключу `MAX_ALLOWED_DISCOUNT_FOR_COUNTERPARTY` — это лимит скидки **контрагенту**, другое понятие, с семантикой «строки нет → безлимит». Join отброшен, используется `COALESCE(cu.max_discount, 0)`.

Следствие для клиента: **`max_discount == 0` значит «персональный потолок не задан», а не «скидка запрещена»** — при нуле ручная скидка не гейтится вовсе (см. Task 21). Иначе сразу после миграции, когда потолок не задан ни у кого, каждая ручная скидка требовала бы PIN старшего.

- [ ] **Step 5: Запустить тест**

Run: `go test ./cashes/ -run TestSellerItemSerializes -v`
Expected: PASS

- [ ] **Step 6: Проверить сборку**

Run: `go build ./... && go vet ./cashes/`
Expected: без ошибок

- [ ] **Step 7: Commit**

```bash
git add cashes/cash_repo.go cashes/seller_roster_test.go
git commit -m "feat: return PIN hash and capability flags in cash seller roster"
```

---

## Task 5: `max_discount` в назначении продавца

**Files:**
- Modify: `cashes/cash_repo.go` (`UpsertCashUser`), `cashes/cash_service.go` (`AssignCashUser`), `cashes/cash_controller.go:69-99` (`AssignUser`), `cashes/cash_interfaces.go`

- [ ] **Step 1: Расширить репозиторий**

Заменить `UpsertCashUser` в `cashes/cash_repo.go`:
```go
func (r *CashRepo) UpsertCashUser(ctx context.Context, db base.PGXDB, cashID, userID string, isSeller, canSell bool, maxDiscount *float64) error {
	_, err := db.Exec(ctx,
		`INSERT INTO cash_users (cash_id, user_id, is_seller, can_sell, max_discount)
		 VALUES ($1, $2, $3, $4, $5)
		 ON CONFLICT (cash_id, user_id) DO UPDATE
		   SET is_seller = EXCLUDED.is_seller,
		       can_sell = EXCLUDED.can_sell,
		       max_discount = EXCLUDED.max_discount`,
		cashID, userID, isSeller, canSell, maxDiscount,
	)
	return err
}
```

`maxDiscount` — указатель, потому что `NULL` означает «наследовать лимит магазина», и это отличается от нуля.

- [ ] **Step 2: Пробросить через сервис и интерфейсы**

Обновить сигнатуру `AssignCashUser` в `cashes/cash_service.go` и соответствующие записи в `cashes/cash_interfaces.go`, добавив параметр `maxDiscount *float64` и передав его в `UpsertCashUser`.

- [ ] **Step 3: Расширить входную структуру контроллера**

В `cashes/cash_controller.go`, в `AssignUser`, заменить `CashUserInput`:
```go
	type CashUserInput struct {
		CashID      string   `json:"cash" binding:"required"`
		UserID      string   `json:"user" binding:"required"`
		IsSeller    bool     `json:"is_seller"`
		CanSell     bool     `json:"can_sell"`
		MaxDiscount *float64 `json:"max_discount"`
	}
```

и передать `s.MaxDiscount` в вызов `ctrl.service.AssignCashUser(...)`.

- [ ] **Step 4: Собрать**

Run: `go build ./... && go test ./cashes/ -v`
Expected: сборка проходит, существующие тесты `cashes` зелёные

- [ ] **Step 5: Commit**

```bash
git add cashes/
git commit -m "feat: accept per-seller max_discount when assigning a cash user"
```

---

## Task 6: `seller` из тела документа

**Files:**
- Modify: `documents/document_expense_repo.go:141-149`, `documents/document_interfaces.go:92`, `documents/document_expense_service.go:75-105`
- Test: `documents/expense_seller_test.go` (создать)

- [ ] **Step 1: Написать падающий тест на выбор продавца**

Логика выбора выносится в чистую функцию, чтобы её можно было проверить без БД.

`documents/expense_seller_test.go`:
```go
package documents

import "testing"

func TestResolveSellerPrefersBodyValue(t *testing.T) {
	got, substituted := resolveSeller("seller-from-body", "jwt-user", true)
	if got != "seller-from-body" {
		t.Errorf("resolveSeller = %q, want %q", got, "seller-from-body")
	}
	if substituted {
		t.Error("substituted = true, want false for an allowed seller")
	}
}

func TestResolveSellerFallsBackToJWTUser(t *testing.T) {
	got, substituted := resolveSeller("", "jwt-user", false)
	if got != "jwt-user" {
		t.Errorf("resolveSeller = %q, want %q", got, "jwt-user")
	}
	if substituted {
		t.Error("substituted = true, want false when the body carried no seller")
	}
}

func TestResolveSellerSubstitutesDisallowedSeller(t *testing.T) {
	got, substituted := resolveSeller("fired-employee", "jwt-user", false)
	if got != "jwt-user" {
		t.Errorf("resolveSeller = %q, want fallback to %q", got, "jwt-user")
	}
	if !substituted {
		t.Error("substituted = false, want true so the document gets flagged")
	}
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `go test ./documents/ -run TestResolveSeller -v`
Expected: FAIL, `undefined: resolveSeller`

- [ ] **Step 3: Реализовать функцию выбора**

В `documents/document_expense_service.go` добавить:
```go
// resolveSeller decides which user is credited with the sale.
//
// A receipt that a cashier already printed and took money for must never be
// rejected at sync time, so a seller who is no longer allowed on this register
// is silently replaced by the shift owner and the document is flagged instead.
// Returns the seller id and whether a substitution happened.
func resolveSeller(bodySeller, jwtUser string, bodySellerAllowed bool) (string, bool) {
	if bodySeller == "" || bodySeller == jwtUser {
		return jwtUser, false
	}
	if bodySellerAllowed {
		return bodySeller, false
	}
	return jwtUser, true
}
```

- [ ] **Step 4: Запустить тесты**

Run: `go test ./documents/ -run TestResolveSeller -v`
Expected: PASS (3 теста)

- [ ] **Step 5: Добавить репозиторный метод проверки продавца**

В `documents/document_expense_repo.go` после `IsCashSeller`:
```go
// IsCashSellerAllowed reports whether the user may be credited as the seller of a
// sale on this register. Unlike IsCashSeller it requires can_sell as well, because
// the cash-register roster (GetSellersForCash) is filtered by can_sell and the two
// flags describe different sets.
func (r *DocumentRepo) IsCashSellerAllowed(ctx context.Context, db base.PGXDB, cashID, userID string) (bool, error) {
	var exists bool
	err := db.QueryRow(ctx,
		`SELECT EXISTS(SELECT 1 FROM cash_users WHERE cash_id=$1 AND user_id=$2 AND is_seller=true AND can_sell=true)`,
		cashID, userID,
	).Scan(&exists)
	return exists, err
}
```

Добавить сигнатуру в `documents/document_interfaces.go` рядом со строкой 92:
```go
	IsCashSellerAllowed(ctx context.Context, db base.PGXDB, cashID, userID string) (bool, error)
```

- [ ] **Step 6: Подключить к созданию документа**

В `documents/document_expense_service.go` заменить блок с `SellerID: userID` (около строки 96) на:
```go
	sellerAllowed := false
	if serializer.SellerID != "" && serializer.SellerID != userID {
		sellerAllowed, err = s.repo.IsCashSellerAllowed(ctx, tx, serializer.CashID, serializer.SellerID)
		if err != nil {
			return nil, base.NewError(http.StatusInternalServerError, "cannot check seller rights", err)
		}
	}
	sellerID, sellerSubstituted := resolveSeller(serializer.SellerID, userID, sellerAllowed)

	de := &DocumentExpense{
		SoldSource:     expenseSoldSource(serializer.SoldSource),
		DocumentBaseID: docBase.ID,
		DocumentBase:   docBase,
		ShiftID:        serializer.ShiftID,
		WarehouseID:    serializer.WarehouseID,
		CashID:         serializer.CashID,
		SellerID:       sellerID,
		QuoteID:        serializer.QuoteID,
	}
```

Сразу после успешного `InsertDocumentExpense` добавить пометку:
```go
	if sellerSubstituted {
		if err := s.repo.MarkBaseSuspicious(ctx, tx, docBase.ID,
			"; seller "+serializer.SellerID+" is not an allowed seller on this cash, credited to shift owner"); err != nil {
			return nil, base.NewError(http.StatusInternalServerError, "cannot flag substituted seller", err)
		}
	}
```

`MarkBaseSuspicious` уже существует в `documents/document_expense_repo.go`.

- [ ] **Step 7: Собрать и прогнать пакет**

Run: `go build ./... && go test ./documents/ -v`
Expected: сборка проходит, тесты зелёные

- [ ] **Step 8: Commit**

```bash
git add documents/
git commit -m "feat: credit sale to seller from request body, flagging disallowed sellers"
```

---

## Task 7: `approved_by` — кто подтвердил операцию сверх прав

**Files:**
- Modify: `documents/serializers.go:113-129`, `documents/document_expense_repo.go` (`InsertDocumentExpense`), `documents/document_interfaces.go`, `documents/document_expense_service.go`

- [ ] **Step 1: Добавить поле в сериализатор**

В `documents/serializers.go`, в `DocumentExpenseSerializer`, после `SellerID`:
```go
	ApprovedBy     string                     `json:"approved_by"`
```

- [ ] **Step 2: Расширить вставку**

В `documents/document_expense_repo.go` заменить `InsertDocumentExpense`:
```go
func (r *DocumentRepo) InsertDocumentExpense(ctx context.Context, db base.PGXDB, docBaseID, warehouseID, cashID, sellerID string, shiftID *string, soldSource expenseSoldSource, quoteID, approvedBy string) (string, error) {
	var id string
	err := db.QueryRow(ctx,
		`INSERT INTO document_expenses (document_base_id, warehouse_id, cash_id, seller_id, shift_id, sold_source, quote_id, approved_by_id)
		 VALUES ($1,$2,$3,$4,$5,$6,NULLIF($7,'')::uuid,NULLIF($8,'')::uuid) RETURNING id`,
		docBaseID, warehouseID, cashID, sellerID, shiftID, soldSource, quoteID, approvedBy,
	).Scan(&id)
	return id, err
}
```

Обновить сигнатуру в `documents/document_interfaces.go`.

- [ ] **Step 3: Передать значение в обоих местах вызова**

В `documents/document_expense_service.go` — две точки вызова (около строк 102 и 247). В первой передать `serializer.ApprovedBy`, во второй (веб-продажа) передать `""`.

- [ ] **Step 4: Собрать и прогнать**

Run: `go build ./... && go test ./documents/ -v`
Expected: сборка проходит, тесты зелёные

- [ ] **Step 5: Commit**

```bash
git add documents/
git commit -m "feat: persist approved_by on expense documents"
```

---

# ФАЗА 2 — Клиент

## Task 8: Проверка PIN-хэша на клиенте

> **РЕАЛИЗОВАНО, форма изменилась при ревью.** `PinHasher.Verify` возвращает не `bool`, а
> `PinVerificationResult { Valid, WrongPin, Malformed }`. Причина: `false` на всё подряд
> означал бы, что испорченная запись в кэше засчитывается счётчику попыток Task 12 как
> промах — и честного продавца блокировали бы за чужую ошибку, которую повтором ввода не
> исправить. `Malformed` описывает **хранимый хэш**, а не ввод: пустой PIN против валидного
> хэша — это `WrongPin`. Также добавлен потолок итераций (испорченное значение в
> нешифрованной SQLite иначе вешает UI на минуты) и зафиксирована реальная фикстура,
> сгенерированная Go. Приведённые ниже сигнатуры тестов — черновик до этих правок;
> фактическая реализация в `src/VvCash/Services/PinHasher.cs`.

**Files:**
- Create: `src/VvCash/Services/PinHasher.cs`
- Test: `tests/VvCash.Tests/PinHasherTest.cs`

- [ ] **Step 1: Написать падающие тесты**

Хэш в тесте — фиксированный, посчитанный тем же алгоритмом, что и на сервере (PBKDF2-HMAC-SHA256, 1000 итераций для скорости теста).

`tests/VvCash.Tests/PinHasherTest.cs`:
```csharp
using System;
using System.Security.Cryptography;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class PinHasherTest
{
    // Builds an encoded hash the same way the Go backend does, so the test proves
    // cross-language compatibility of the format rather than self-consistency.
    private static string Encode(string pin, int iterations = 1000)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        using var kdf = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256);
        var key = kdf.GetBytes(32);
        return $"pbkdf2_sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    [Fact]
    public void Verify_AcceptsCorrectPin()
    {
        Assert.True(PinHasher.Verify("4821", Encode("4821")));
    }

    [Fact]
    public void Verify_RejectsWrongPin()
    {
        Assert.False(PinHasher.Verify("4822", Encode("4821")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("pbkdf2_sha256$abc$c2FsdA==$aGFzaA==")]
    [InlineData("argon2$1$2$3")]
    [InlineData("pbkdf2_sha256$1000$!!!notbase64!!!$aGFzaA==")]
    public void Verify_RejectsMalformedHash(string encoded)
    {
        Assert.False(PinHasher.Verify("4821", encoded));
    }

    [Fact]
    public void Verify_RejectsEmptyPinAgainstValidHash()
    {
        Assert.False(PinHasher.Verify("", Encode("4821")));
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~PinHasherTest`
Expected: FAIL — компиляция, `The name 'PinHasher' does not exist`

- [ ] **Step 3: Реализовать**

`src/VvCash/Services/PinHasher.cs`:
```csharp
using System;
using System.Security.Cryptography;

namespace VvCash.Services;

/// <summary>Verifies PBKDF2-HMAC-SHA256 PIN hashes produced by the backend.
/// Format: pbkdf2_sha256$iterations$base64salt$base64hash.
/// Verify only — the cash register never creates hashes, the server does.</summary>
public static class PinHasher
{
    public static bool Verify(string pin, string encoded)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(encoded)) return false;

        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2_sha256") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt, want;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            want = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (want.Length == 0) return false;

        using var kdf = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256);
        var got = kdf.GetBytes(want.Length);
        return CryptographicOperations.FixedTimeEquals(got, want);
    }
}
```

- [ ] **Step 4: Запустить тесты**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~PinHasherTest`
Expected: PASS (8 тестов)

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/PinHasher.cs tests/VvCash.Tests/PinHasherTest.cs
git commit -m "feat: verify PBKDF2 PIN hashes on the cash register"
```

---

## Task 9: Модель `SellerInfo`

**Files:**
- Create: `src/VvCash/Models/SellerInfo.cs`

- [ ] **Step 1: Написать модель**

`src/VvCash/Models/SellerInfo.cs`:
```csharp
using System.Text.Json.Serialization;

namespace VvCash.Models;

/// <summary>A seller on the roster of this cash register, as returned by GET /cashes/seller/.
/// PinHash is cached locally so switching sellers works with no network.</summary>
public class SellerInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("first_name")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("last_name")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("pin_hash")]
    public string PinHash { get; set; } = string.Empty;

    [JsonPropertyName("can_sell")]
    public bool CanSell { get; set; }

    [JsonPropertyName("can_refund")]
    public bool CanRefund { get; set; }

    [JsonPropertyName("can_close_shift")]
    public bool CanCloseShift { get; set; }

    [JsonPropertyName("max_discount")]
    public decimal MaxDiscount { get; set; }

    [JsonIgnore]
    public string FullName => $"{FirstName} {LastName}".Trim();

    [JsonIgnore]
    public bool HasPin => !string.IsNullOrEmpty(PinHash);
}
```

- [ ] **Step 2: Собрать**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/VvCash/Models/SellerInfo.cs
git commit -m "feat: add SellerInfo model for the cash register roster"
```

---

## Task 10: Кэш продавцов в SQLite

**Files:**
- Modify: `src/VvCash/Services/Data/IOfflineStorageService.cs`, `src/VvCash/Services/Data/OfflineStorageService.cs:33-78`

- [ ] **Step 1: Добавить таблицу в схему**

В `OfflineStorageService.InitializeAsync`, в блок `command.CommandText`, перед комментарием `-- Create indices for performance`:
```sql
            CREATE TABLE IF NOT EXISTS Sellers (
                Id TEXT PRIMARY KEY,
                FirstName TEXT NOT NULL,
                LastName TEXT,
                PinHash TEXT,
                CanSell INTEGER NOT NULL DEFAULT 1,
                CanRefund INTEGER NOT NULL DEFAULT 0,
                CanCloseShift INTEGER NOT NULL DEFAULT 0,
                MaxDiscount REAL NOT NULL DEFAULT 0
            );
```

`CREATE TABLE IF NOT EXISTS` делает это безопасным для уже развёрнутых касс — существующая БД просто дополняется.

- [ ] **Step 2: Расширить интерфейс**

В `src/VvCash/Services/Data/IOfflineStorageService.cs`, после блока категорий:
```csharp
    Task SaveSellersAsync(IEnumerable<SellerInfo> sellers);
    Task<IEnumerable<SellerInfo>> GetSellersAsync();
```

- [ ] **Step 3: Реализовать методы**

В `OfflineStorageService.cs` добавить (пакетная вставка повторяет стиль `SaveProductsAsync`):
```csharp
    public async Task SaveSellersAsync(IEnumerable<SellerInfo> sellers)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM Sellers";
            await clear.ExecuteNonQueryAsync();
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Sellers (Id, FirstName, LastName, PinHash, CanSell, CanRefund, CanCloseShift, MaxDiscount)
            VALUES ($Id, $FirstName, $LastName, $PinHash, $CanSell, $CanRefund, $CanCloseShift, $MaxDiscount)";

        var idParam = command.Parameters.Add("$Id", SqliteType.Text);
        var firstParam = command.Parameters.Add("$FirstName", SqliteType.Text);
        var lastParam = command.Parameters.Add("$LastName", SqliteType.Text);
        var pinParam = command.Parameters.Add("$PinHash", SqliteType.Text);
        var sellParam = command.Parameters.Add("$CanSell", SqliteType.Integer);
        var refundParam = command.Parameters.Add("$CanRefund", SqliteType.Integer);
        var closeParam = command.Parameters.Add("$CanCloseShift", SqliteType.Integer);
        var discountParam = command.Parameters.Add("$MaxDiscount", SqliteType.Real);

        foreach (var s in sellers)
        {
            idParam.Value = s.Id;
            firstParam.Value = s.FirstName;
            lastParam.Value = (object?)s.LastName ?? DBNull.Value;
            pinParam.Value = (object?)s.PinHash ?? DBNull.Value;
            sellParam.Value = s.CanSell ? 1 : 0;
            refundParam.Value = s.CanRefund ? 1 : 0;
            closeParam.Value = s.CanCloseShift ? 1 : 0;
            discountParam.Value = (double)s.MaxDiscount;
            await command.ExecuteNonQueryAsync();
        }

        transaction.Commit();
    }

    public async Task<IEnumerable<SellerInfo>> GetSellersAsync()
    {
        var result = new List<SellerInfo>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, FirstName, LastName, PinHash, CanSell, CanRefund, CanCloseShift, MaxDiscount FROM Sellers";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new SellerInfo
            {
                Id = reader.GetString(0),
                FirstName = reader.GetString(1),
                LastName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                PinHash = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                CanSell = reader.GetInt32(4) == 1,
                CanRefund = reader.GetInt32(5) == 1,
                CanCloseShift = reader.GetInt32(6) == 1,
                MaxDiscount = (decimal)reader.GetDouble(7)
            });
        }

        return result;
    }
```

- [ ] **Step 4: Собрать**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/Data/IOfflineStorageService.cs src/VvCash/Services/Data/OfflineStorageService.cs
git commit -m "feat: cache the seller roster in local SQLite"
```

---

## Task 11: Загрузка ростера с сервера

**Files:**
- Create: `src/VvCash/Services/Api/ISellerRosterService.cs`, `src/VvCash/Services/Api/SellerRosterService.cs`
- Test: `tests/VvCash.Tests/SellerRosterServiceTest.cs`

- [ ] **Step 1: Написать падающий тест**

`StubHttpMessageHandler` уже есть в `tests/VvCash.Tests/StubHttpMessageHandler.cs` — перед написанием теста открыть его и использовать фактическую сигнатуру конструктора.

`tests/VvCash.Tests/SellerRosterServiceTest.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

public class SellerRosterServiceTest
{
    private const string RosterJson = """
    {"status":0,"body":[
      {"id":"u-1","first_name":"Азиз","last_name":"Каримов","pin_hash":"pbkdf2_sha256$1000$c2FsdA==$aGFzaA==",
       "can_sell":true,"can_refund":true,"can_close_shift":false,"max_discount":15},
      {"id":"u-2","first_name":"Дилноза","last_name":"Юсупова","pin_hash":"",
       "can_sell":true,"can_refund":false,"can_close_shift":false,"max_discount":0}
    ]}
    """;

    private sealed class FakeStorage : IOfflineStorageServiceSellersOnly
    {
        public List<SellerInfo> Saved { get; } = new();
        public Task SaveSellersAsync(IEnumerable<SellerInfo> sellers)
        {
            Saved.Clear();
            Saved.AddRange(sellers);
            return Task.CompletedTask;
        }
        public Task<IEnumerable<SellerInfo>> GetSellersAsync() => Task.FromResult<IEnumerable<SellerInfo>>(Saved);
    }

    [Fact]
    public async Task RefreshAsync_ParsesRosterAndCachesIt()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, RosterJson);
        var client = new HttpClient(handler);
        var settings = new FakeSettingsService { BackendUrl = "https://example.test" };
        var storage = new FakeStorage();
        var service = new SellerRosterService(client, settings, storage);

        var sellers = (await service.RefreshAsync()).ToList();

        Assert.Equal(2, sellers.Count);
        Assert.Equal("Азиз Каримов", sellers[0].FullName);
        Assert.True(sellers[0].CanRefund);
        Assert.Equal(15m, sellers[0].MaxDiscount);
        Assert.False(sellers[1].HasPin);
        Assert.Equal(2, storage.Saved.Count);
    }

    [Fact]
    public async Task RefreshAsync_OnNetworkFailure_ReturnsCachedRoster()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var client = new HttpClient(handler);
        var settings = new FakeSettingsService { BackendUrl = "https://example.test" };
        var storage = new FakeStorage();
        storage.Saved.Add(new SellerInfo { Id = "cached", FirstName = "Кэш" });
        var service = new SellerRosterService(client, settings, storage);

        var sellers = (await service.RefreshAsync()).ToList();

        Assert.Single(sellers);
        Assert.Equal("cached", sellers[0].Id);
    }
}
```

**Перед реализацией:** в тесте использованы `FakeSettingsService` и `IOfflineStorageServiceSellersOnly` как заглушки. Вместо изобретения нового интерфейса — посмотреть, как существующие тесты (`SyncServiceTest.cs`, `QuoteServiceTest.cs`) подменяют `ISettingsService` и `IOfflineStorageService`, и повторить тот же приём: если там используется полная реализация-заглушка, написать `FakeStorage : IOfflineStorageService` с `NotImplementedException` в неиспользуемых методах и убрать `IOfflineStorageServiceSellersOnly` из теста.

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~SellerRosterServiceTest`
Expected: FAIL — компиляция, `The name 'SellerRosterService' does not exist`

- [ ] **Step 3: Написать интерфейс**

`src/VvCash/Services/Api/ISellerRosterService.cs`:
```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Api;

public interface ISellerRosterService
{
    /// <summary>Fetches the roster from the server and caches it. On any network or
    /// parse failure returns the cached roster instead, so the register keeps working.</summary>
    Task<IEnumerable<SellerInfo>> RefreshAsync();

    /// <summary>Returns the cached roster without touching the network.</summary>
    Task<IEnumerable<SellerInfo>> GetCachedAsync();
}
```

- [ ] **Step 4: Реализовать сервис**

`src/VvCash/Services/Api/SellerRosterService.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Data;

namespace VvCash.Services.Api;

public class SellerRosterService : ISellerRosterService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _storage;

    public SellerRosterService(HttpClient httpClient, ISettingsService settingsService, IOfflineStorageService storage)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _storage = storage;
    }

    public Task<IEnumerable<SellerInfo>> GetCachedAsync() => _storage.GetSellersAsync();

    public async Task<IEnumerable<SellerInfo>> RefreshAsync()
    {
        try
        {
            var baseUrl = _settingsService.BackendUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return await GetCachedAsync();
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            var response = await _httpClient.GetAsync($"{baseUrl}cashes/seller/");
            if (!response.IsSuccessStatusCode) return await GetCachedAsync();

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetInt32() != 0)
                return await GetCachedAsync();
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
                return await GetCachedAsync();

            var sellers = JsonSerializer.Deserialize<List<SellerInfo>>(body.GetRawText()) ?? new List<SellerInfo>();
            await _storage.SaveSellersAsync(sellers);
            return sellers;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SellerRosterService] Refresh failed, falling back to cache: {ex.Message}");
            return await GetCachedAsync();
        }
    }
}
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~SellerRosterServiceTest`
Expected: PASS (2 теста)

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/Services/Api/ISellerRosterService.cs src/VvCash/Services/Api/SellerRosterService.cs tests/VvCash.Tests/SellerRosterServiceTest.cs
git commit -m "feat: fetch and cache the cash-register seller roster"
```

---

## Task 12: Сессия продавца — переключение, блокировка, таймаут

**Files:**
- Create: `src/VvCash/Services/ISellerSession.cs`, `src/VvCash/Services/SellerSession.cs`
- Test: `tests/VvCash.Tests/SellerSessionTest.cs`

Время подаётся извне (`Func<DateTime>`), иначе таймаут и блокировку нельзя протестировать без `Thread.Sleep`.

- [ ] **Step 1: Написать падающие тесты**

`tests/VvCash.Tests/SellerSessionTest.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class SellerSessionTest
{
    private static string Encode(string pin)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        using var kdf = new Rfc2898DeriveBytes(pin, salt, 1000, HashAlgorithmName.SHA256);
        return $"pbkdf2_sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(kdf.GetBytes(32))}";
    }

    private static List<SellerInfo> Roster() => new()
    {
        new SellerInfo { Id = "u-1", FirstName = "Азиз", PinHash = Encode("4821"), CanSell = true, MaxDiscount = 15 },
        new SellerInfo { Id = "u-2", FirstName = "Дилноза", PinHash = Encode("9073"), CanSell = true }
    };

    private static SellerSession NewSession(Func<DateTime> clock)
        => new(clock, TimeSpan.FromSeconds(90));

    [Fact]
    public async Task SwitchAsync_WithCorrectPin_SetsCurrentSeller()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var result = await session.SwitchAsync("u-1", "4821");

        Assert.Equal(SwitchResult.Ok, result);
        Assert.Equal("u-1", session.Current?.Id);
    }

    [Fact]
    public async Task SwitchAsync_WithWrongPin_LeavesCurrentUnchanged()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var result = await session.SwitchAsync("u-1", "0000");

        Assert.Equal(SwitchResult.WrongPin, result);
        Assert.Null(session.Current);
    }

    [Fact]
    public async Task SwitchAsync_LocksSellerAfterFiveFailures()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        for (var i = 0; i < 5; i++)
            await session.SwitchAsync("u-1", "0000");

        Assert.Equal(SwitchResult.Locked, await session.SwitchAsync("u-1", "4821"));
        Assert.Equal(SwitchResult.Ok, await session.SwitchAsync("u-2", "9073"));
    }

    [Fact]
    public async Task SwitchAsync_LockExpiresAfterSixtySeconds()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        for (var i = 0; i < 5; i++)
            await session.SwitchAsync("u-1", "0000");

        now = now.AddSeconds(61);

        Assert.Equal(SwitchResult.Ok, await session.SwitchAsync("u-1", "4821"));
    }

    [Fact]
    public async Task IsStale_BecomesTrueAfterIdleTimeout()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        Assert.False(session.IsStale);

        now = now.AddSeconds(91);

        Assert.True(session.IsStale);
    }

    [Fact]
    public async Task Touch_ResetsIdleTimer()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        now = now.AddSeconds(80);
        session.Touch();
        now = now.AddSeconds(80);

        Assert.False(session.IsStale);
    }

    [Fact]
    public async Task SwitchAsync_RaisesCurrentChanged()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var raised = 0;
        session.CurrentChanged += (_, _) => raised++;

        await session.SwitchAsync("u-1", "4821");
        await session.SwitchAsync("u-1", "0000");

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task SwitchAsync_ForSellerWithoutPin_ReturnsPinNotSet()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-3", FirstName = "Новичок", PinHash = "", CanSell = true }
        });

        Assert.Equal(SwitchResult.PinNotSet, await session.SwitchAsync("u-3", "4821"));
    }

    [Fact]
    public async Task SwitchAsync_WithCorruptHash_DoesNotCountTowardLockout()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-4", FirstName = "Битый", PinHash = "not-a-valid-hash", CanSell = true }
        });

        for (var i = 0; i < 6; i++)
            Assert.Equal(SwitchResult.CorruptHash, await session.SwitchAsync("u-4", "4821"));
    }

    [Fact]
    public async Task Clear_DropsCurrentSeller()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        session.Clear();

        Assert.Null(session.Current);
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~SellerSessionTest`
Expected: FAIL — компиляция, `The name 'SellerSession' does not exist`

- [ ] **Step 3: Написать интерфейс**

`src/VvCash/Services/ISellerSession.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

public enum SwitchResult
{
    Ok,
    WrongPin,
    Locked,
    PinNotSet,
    UnknownSeller,

    /// <summary>The cached hash is unusable — a corrupt row, not a bad guess.
    /// No PIN can succeed until the roster is refreshed.</summary>
    CorruptHash
}

public interface ISellerSession
{
    SellerInfo? Current { get; }

    /// <summary>True when the idle timeout elapsed and the seller must be re-confirmed.</summary>
    bool IsStale { get; }

    IReadOnlyList<SellerInfo> Roster { get; }

    event EventHandler? CurrentChanged;

    Task LoadRosterAsync(IEnumerable<SellerInfo> sellers);
    Task<SwitchResult> SwitchAsync(string sellerId, string pin);

    /// <summary>Verifies a PIN for an escalation without changing the current seller.
    /// Returns the approving seller on success, null otherwise.</summary>
    Task<SellerInfo?> ApproveAsync(string sellerId, string pin);

    /// <summary>Resets the idle timer — called on any register activity.</summary>
    void Touch();

    void Clear();
}
```

- [ ] **Step 4: Реализовать**

`src/VvCash/Services/SellerSession.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

/// <summary>Tracks who is currently selling at this register. PINs are verified
/// against the locally cached roster, so switching never touches the network.
///
/// The PIN is not a security boundary — see the design spec. It guards against
/// ringing up a sale under a colleague's name, nothing more.</summary>
public class SellerSession : ISellerSession
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);

    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _idleTimeout;
    private readonly Dictionary<string, int> _failures = new();
    private readonly Dictionary<string, DateTime> _lockedUntil = new();

    private List<SellerInfo> _roster = new();
    private DateTime _lastActivity;

    public SellerSession() : this(() => DateTime.UtcNow, TimeSpan.FromSeconds(90)) { }

    public SellerSession(Func<DateTime> clock, TimeSpan idleTimeout)
    {
        _clock = clock;
        _idleTimeout = idleTimeout;
        _lastActivity = clock();
    }

    public SellerInfo? Current { get; private set; }

    public IReadOnlyList<SellerInfo> Roster => _roster;

    public bool IsStale => Current == null || _clock() - _lastActivity > _idleTimeout;

    public event EventHandler? CurrentChanged;

    public Task LoadRosterAsync(IEnumerable<SellerInfo> sellers)
    {
        _roster = sellers.ToList();
        return Task.CompletedTask;
    }

    public Task<SwitchResult> SwitchAsync(string sellerId, string pin)
    {
        var (result, seller) = Check(sellerId, pin);
        if (result != SwitchResult.Ok) return Task.FromResult(result);

        Current = seller;
        _lastActivity = _clock();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(SwitchResult.Ok);
    }

    public Task<SellerInfo?> ApproveAsync(string sellerId, string pin)
    {
        var (result, seller) = Check(sellerId, pin);
        return Task.FromResult(result == SwitchResult.Ok ? seller : null);
    }

    public void Touch() => _lastActivity = _clock();

    public void Clear()
    {
        if (Current == null) return;
        Current = null;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    private (SwitchResult, SellerInfo?) Check(string sellerId, string pin)
    {
        var seller = _roster.FirstOrDefault(s => s.Id == sellerId);
        if (seller == null) return (SwitchResult.UnknownSeller, null);
        if (!seller.HasPin) return (SwitchResult.PinNotSet, null);

        if (_lockedUntil.TryGetValue(sellerId, out var until))
        {
            if (_clock() < until) return (SwitchResult.Locked, null);
            _lockedUntil.Remove(sellerId);
            _failures.Remove(sellerId);
        }

        // Only a genuinely wrong PIN counts toward the lockout. A corrupt cached
        // hash would otherwise lock out a seller who typed correctly, for a fault
        // that is not theirs and that retrying cannot clear.
        switch (PinHasher.Verify(pin, seller.PinHash))
        {
            case PinVerificationResult.Malformed:
                return (SwitchResult.CorruptHash, null);

            case PinVerificationResult.WrongPin:
                var count = _failures.TryGetValue(sellerId, out var c) ? c + 1 : 1;
                _failures[sellerId] = count;
                if (count >= MaxFailures) _lockedUntil[sellerId] = _clock() + LockDuration;
                return (SwitchResult.WrongPin, null);
        }

        _failures.Remove(sellerId);
        return (SwitchResult.Ok, seller);
    }
}
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~SellerSessionTest`
Expected: PASS (9 тестов)

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/Services/ISellerSession.cs src/VvCash/Services/SellerSession.cs tests/VvCash.Tests/SellerSessionTest.cs
git commit -m "feat: add offline seller session with PIN switching and lockout"
```

---

## Task 13: Регистрация в DI

**Files:**
- Modify: `src/VvCash/App.axaml.cs:113-167`

- [ ] **Step 1: Зарегистрировать сервисы**

В `ConfigureServices`, после `services.AddSingleton<ISessionContext, SessionContext>();`:
```csharp
        services.AddSingleton<ISellerSession, SellerSession>();
```

После строки регистрации `IShiftService`:
```csharp
        services.AddHttpClient<ISellerRosterService, SellerRosterService>().AddHttpMessageHandler<AuthHeaderHandler>();
```

Добавить `services.AddTransient<SellerSwitchViewModel>();` в блок ViewModels — сама VM появляется в Task 14, поэтому эта строка добавляется там же; здесь только сервисы.

- [ ] **Step 2: Собрать**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/VvCash/App.axaml.cs
git commit -m "feat: register seller session and roster services"
```

---

## Task 14: ViewModel оверлея выбора продавца

**Files:**
- Create: `src/VvCash/ViewModels/SellerSwitchViewModel.cs`
- Modify: `src/VvCash/App.axaml.cs` (регистрация VM)
- Test: `tests/VvCash.Tests/SellerSwitchViewModelTest.cs`

- [ ] **Step 1: Написать падающие тесты**

`tests/VvCash.Tests/SellerSwitchViewModelTest.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class SellerSwitchViewModelTest
{
    private static string Encode(string pin)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        using var kdf = new Rfc2898DeriveBytes(pin, salt, 1000, HashAlgorithmName.SHA256);
        return $"pbkdf2_sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(kdf.GetBytes(32))}";
    }

    private static async Task<SellerSession> SessionWithRoster()
    {
        var session = new SellerSession(() => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(90));
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-1", FirstName = "Азиз", PinHash = Encode("4821"), CanSell = true },
            new() { Id = "u-2", FirstName = "Дилноза", PinHash = Encode("9073"), CanSell = true, CanCloseShift = true }
        });
        return session;
    }

    [Fact]
    public async Task SelectSeller_MovesToPinEntry()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster());
        vm.Open();

        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        Assert.True(vm.IsPinEntry);
        Assert.Equal("Азиз", vm.SelectedSeller?.FirstName);
    }

    [Fact]
    public async Task AppendDigit_BuildsPinAndAutoSubmitsAtFourDigits()
    {
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session);
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "4821")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.Equal("u-1", session.Current?.Id);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task WrongPin_ShowsErrorAndClearsInput()
    {
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session);
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "0000")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.True(vm.HasError);
        Assert.Equal(string.Empty, vm.Pin);
        Assert.True(vm.IsVisible);
        Assert.Null(session.Current);
    }

    [Fact]
    public async Task Backspace_RemovesLastDigit()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        await vm.AppendDigitCommand.ExecuteAsync("4");
        await vm.AppendDigitCommand.ExecuteAsync("8");
        vm.BackspaceCommand.Execute(null);

        Assert.Equal("4", vm.Pin);
    }

    [Fact]
    public async Task OpenForApproval_ListsOnlySellersWithTheRight()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster());

        vm.OpenForApproval(s => s.CanCloseShift);

        Assert.Single(vm.Sellers);
        Assert.Equal("u-2", vm.Sellers[0].Id);
    }

    [Fact]
    public async Task Approval_DoesNotChangeCurrentSeller()
    {
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session);

        SellerInfo? approver = null;
        vm.Approved += (_, s) => approver = s;
        vm.OpenForApproval(s => s.CanCloseShift);
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "9073")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.Equal("u-2", approver?.Id);
        Assert.Equal("u-1", session.Current?.Id);
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~SellerSwitchViewModelTest`
Expected: FAIL — компиляция, `The name 'SellerSwitchViewModel' does not exist`

- [ ] **Step 3: Реализовать VM**

`src/VvCash/ViewModels/SellerSwitchViewModel.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Services;

namespace VvCash.ViewModels;

/// <summary>Drives the seller-switch overlay. Two modes: switching the current seller,
/// and approving an operation the current seller lacks the right for — the latter never
/// changes who is selling.</summary>
public partial class SellerSwitchViewModel : ViewModelBase
{
    private const int PinLength = 4;

    private readonly ISellerSession _session;
    private bool _approvalMode;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isPinEntry;
    [ObservableProperty] private SellerInfo? _selectedSeller;
    [ObservableProperty] private string _pin = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public ObservableCollection<SellerInfo> Sellers { get; } = new();

    /// <summary>Raised when an escalation PIN was accepted, carrying the approving seller.</summary>
    public event EventHandler<SellerInfo>? Approved;

    public SellerSwitchViewModel(ISellerSession session)
    {
        _session = session;
    }

    public void Open() => Show(_ => true, approvalMode: false);

    public void OpenForApproval(Func<SellerInfo, bool> hasRight) => Show(hasRight, approvalMode: true);

    private void Show(Func<SellerInfo, bool> filter, bool approvalMode)
    {
        _approvalMode = approvalMode;
        Sellers.Clear();
        foreach (var s in _session.Roster.Where(filter))
            Sellers.Add(s);

        SelectedSeller = null;
        Pin = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
        IsPinEntry = false;
        IsVisible = true;
    }

    [RelayCommand]
    private void SelectSeller(SellerInfo seller)
    {
        SelectedSeller = seller;
        Pin = string.Empty;
        HasError = false;
        IsPinEntry = true;
    }

    [RelayCommand]
    private async Task AppendDigitAsync(string digit)
    {
        if (SelectedSeller == null || Pin.Length >= PinLength) return;

        HasError = false;
        Pin += digit;

        if (Pin.Length == PinLength)
            await SubmitAsync();
    }

    [RelayCommand]
    private void Backspace()
    {
        if (Pin.Length > 0) Pin = Pin[..^1];
        HasError = false;
    }

    [RelayCommand]
    private void Back()
    {
        IsPinEntry = false;
        SelectedSeller = null;
        Pin = string.Empty;
        HasError = false;
    }

    private async Task SubmitAsync()
    {
        if (SelectedSeller == null) return;

        if (_approvalMode)
        {
            var approver = await _session.ApproveAsync(SelectedSeller.Id, Pin);
            if (approver == null)
            {
                Fail(I18nService.Instance["SellerPinWrong"]);
                return;
            }
            IsVisible = false;
            Approved?.Invoke(this, approver);
            return;
        }

        var result = await _session.SwitchAsync(SelectedSeller.Id, Pin);
        switch (result)
        {
            case SwitchResult.Ok:
                IsVisible = false;
                break;
            case SwitchResult.Locked:
                Fail(I18nService.Instance["SellerLocked"]);
                break;
            case SwitchResult.PinNotSet:
                Fail(I18nService.Instance["SellerPinNotSet"]);
                break;
            default:
                Fail(I18nService.Instance["SellerPinWrong"]);
                break;
        }
    }

    private void Fail(string message)
    {
        Pin = string.Empty;
        HasError = true;
        ErrorMessage = message;
    }
}
```

- [ ] **Step 4: Зарегистрировать VM в DI**

В `src/VvCash/App.axaml.cs`, в блок ViewModels:
```csharp
        services.AddTransient<SellerSwitchViewModel>();
```

- [ ] **Step 5: Добавить строки локализации**

В каждый из `src/VvCash/Assets/i18n/{ru,en,uz,tg,kk}.json` добавить ключи. Русский:
```json
  "SelectSeller": "Кто продаёт?",
  "EnterPin": "Введите PIN",
  "SellerPinWrong": "Неверный PIN",
  "SellerLocked": "Заблокировано на минуту",
  "SellerPinNotSet": "PIN не задан",
  "ConfirmWithPin": "Подтвердите PIN",
  "CurrentSeller": "Продавец",
```

Английский:
```json
  "SelectSeller": "Who is selling?",
  "EnterPin": "Enter PIN",
  "SellerPinWrong": "Wrong PIN",
  "SellerLocked": "Locked for a minute",
  "SellerPinNotSet": "PIN is not set",
  "ConfirmWithPin": "Confirm with PIN",
  "CurrentSeller": "Seller",
```

Для `uz`, `tg`, `kk` — перевести те же семь ключей; если переводчика нет, скопировать русские значения (в файлах уже встречаются русские строки как заглушки, см. ключ `"Поискклиента"`).

- [ ] **Step 6: Запустить тесты**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~SellerSwitchViewModelTest`
Expected: PASS (6 тестов)

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/ViewModels/SellerSwitchViewModel.cs src/VvCash/App.axaml.cs src/VvCash/Assets/i18n/ tests/VvCash.Tests/SellerSwitchViewModelTest.cs
git commit -m "feat: add seller switch overlay view model"
```

---

## Task 15: Вёрстка оверлея

**Files:**
- Create: `src/VvCash/Views/SellerSwitchView.axaml`, `src/VvCash/Views/SellerSwitchView.axaml.cs`

- [ ] **Step 1: Написать разметку**

`src/VvCash/Views/SellerSwitchView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:VvCash.ViewModels"
             xmlns:models="using:VvCash.Models"
             x:Class="VvCash.Views.SellerSwitchView"
             x:DataType="vm:SellerSwitchViewModel"
             IsVisible="{Binding IsVisible}">

  <Border Background="#CC000000">
    <Border Background="{DynamicResource SystemChromeLowColor}"
            CornerRadius="16" Padding="32"
            MaxWidth="640" VerticalAlignment="Center" HorizontalAlignment="Center">

      <!-- Step 1: pick a seller -->
      <StackPanel IsVisible="{Binding !IsPinEntry}" Spacing="24">
        <TextBlock Text="{Binding Source={x:Static vm:I18nBinding.Instance}, Path=[SelectSeller]}"
                   FontSize="24" FontWeight="SemiBold" HorizontalAlignment="Center"/>
        <ItemsControl ItemsSource="{Binding Sellers}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
              <WrapPanel HorizontalAlignment="Center"/>
            </ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="models:SellerInfo">
              <Button Width="180" Height="120" Margin="8"
                      Command="{Binding $parent[ItemsControl].((vm:SellerSwitchViewModel)DataContext).SelectSellerCommand}"
                      CommandParameter="{Binding}">
                <TextBlock Text="{Binding FullName}" FontSize="18"
                           TextWrapping="Wrap" TextAlignment="Center"/>
              </Button>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </StackPanel>

      <!-- Step 2: enter the PIN -->
      <StackPanel IsVisible="{Binding IsPinEntry}" Spacing="16" Width="320">
        <TextBlock Text="{Binding SelectedSeller.FullName}" FontSize="22"
                   FontWeight="SemiBold" HorizontalAlignment="Center"/>
        <TextBlock Text="{Binding ErrorMessage}" IsVisible="{Binding HasError}"
                   Foreground="#E53935" HorizontalAlignment="Center"/>
        <TextBox Text="{Binding Pin}" IsReadOnly="True" PasswordChar="●"
                 FontSize="28" TextAlignment="Center" MaxLength="4"/>
        <UniformGrid Columns="3" Rows="4">
          <Button Content="1" Command="{Binding AppendDigitCommand}" CommandParameter="1" Margin="4" Height="64"/>
          <Button Content="2" Command="{Binding AppendDigitCommand}" CommandParameter="2" Margin="4" Height="64"/>
          <Button Content="3" Command="{Binding AppendDigitCommand}" CommandParameter="3" Margin="4" Height="64"/>
          <Button Content="4" Command="{Binding AppendDigitCommand}" CommandParameter="4" Margin="4" Height="64"/>
          <Button Content="5" Command="{Binding AppendDigitCommand}" CommandParameter="5" Margin="4" Height="64"/>
          <Button Content="6" Command="{Binding AppendDigitCommand}" CommandParameter="6" Margin="4" Height="64"/>
          <Button Content="7" Command="{Binding AppendDigitCommand}" CommandParameter="7" Margin="4" Height="64"/>
          <Button Content="8" Command="{Binding AppendDigitCommand}" CommandParameter="8" Margin="4" Height="64"/>
          <Button Content="9" Command="{Binding AppendDigitCommand}" CommandParameter="9" Margin="4" Height="64"/>
          <Button Content="←" Command="{Binding BackCommand}" Margin="4" Height="64"/>
          <Button Content="0" Command="{Binding AppendDigitCommand}" CommandParameter="0" Margin="4" Height="64"/>
          <Button Content="⌫" Command="{Binding BackspaceCommand}" Margin="4" Height="64"/>
        </UniformGrid>
      </StackPanel>

    </Border>
  </Border>
</UserControl>
```

**Внимание:** привязка команды из шаблона элемента к команде VM — известная ловушка Avalonia в этом проекте. Приводить `DataContext` предка к типу VM внутри `DataTemplate` компилируется, но падает в рантайме. Перед написанием этого файла посмотреть, как то же самое сделано в [`PosView.axaml`](../../../src/VvCash/Views/PosView.axaml) для кнопок товара, и повторить рабочий приём оттуда, а не изобретать. Аналогично `I18nBinding` — в разметке использовать тот способ локализации, который уже применяется в `PosView.axaml`/`LoginView.axaml`.

- [ ] **Step 2: Написать code-behind**

`src/VvCash/Views/SellerSwitchView.axaml.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VvCash.Views;

public partial class SellerSwitchView : UserControl
{
    public SellerSwitchView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
```

Если в проекте code-behind для `UserControl` пишется иначе (сверить с `MixedPaymentView.axaml.cs`) — повторить существующий стиль.

- [ ] **Step 3: Собрать**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: Build succeeded, без XAML-ошибок

- [ ] **Step 4: Commit**

```bash
git add src/VvCash/Views/SellerSwitchView.axaml src/VvCash/Views/SellerSwitchView.axaml.cs
git commit -m "feat: add seller switch overlay view"
```

---

## Task 16: Подключение к POS — `seller_id` в чеке и гейт при первом товаре

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs` (конструктор, `AddToCart` ~736, `Pay` ~1053, `Dispose`)
- Modify: `src/VvCash/Views/PosView.axaml`
- Test: `tests/VvCash.Tests/DocumentRequestSellerTest.cs`

- [ ] **Step 1: Написать падающий тест на сериализацию**

`tests/VvCash.Tests/DocumentRequestSellerTest.cs`:
```csharp
using System.Text.Json;
using VvCash.Models.Api;
using Xunit;

namespace VvCash.Tests;

public class DocumentRequestSellerTest
{
    [Fact]
    public void Serialize_IncludesSellerIdWhenSet()
    {
        var request = new DocumentRequest { DocumentHash = "h1", ShiftId = "s1", SellerId = "u-1" };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"seller_id\":\"u-1\"", json);
    }

    [Fact]
    public void Serialize_OmitsSellerIdWhenNull()
    {
        var request = new DocumentRequest { DocumentHash = "h1", ShiftId = "s1", SellerId = null };

        var json = JsonSerializer.Serialize(request);

        Assert.DoesNotContain("seller_id", json);
    }
}
```

- [ ] **Step 2: Запустить — проверить результат**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~DocumentRequestSellerTest`
Expected: первый тест PASS, второй PASS — поле уже помечено `JsonIgnoreCondition.WhenWritingNull`.

**Если оба прошли сразу** — это ожидаемо: модель уже готова, тест фиксирует контракт от регрессии. Двигаться дальше.

- [ ] **Step 3: Привести имя поля в соответствие с сервером**

Сервер читает `json:"seller"` (`documents/serializers.go`), а клиент шлёт `seller_id`. Выбрать одну сторону. Правильнее поправить **клиент**, чтобы не ломать веб-клиентов сервера:

в `src/VvCash/Models/Api/DocumentRequest.cs` заменить
```csharp
    [JsonPropertyName("seller_id")]
```
на
```csharp
    [JsonPropertyName("seller")]
```

и в тесте из Step 1 заменить `"seller_id"` на `"seller"` в обоих утверждениях.

- [ ] **Step 4: Внедрить сессию в PosViewModel**

Добавить поле и параметр конструктора:
```csharp
    private readonly ISellerSession _sellerSession;
```
В конструкторе — присвоение и подписка:
```csharp
        _sellerSession = sellerSession;
        _sellerSession.CurrentChanged += OnSellerChanged;
```
Добавить обработчик и свойство для чипа:
```csharp
    public string CurrentSellerName => _sellerSession.Current?.FullName ?? string.Empty;

    private void OnSellerChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(CurrentSellerName));
```
В существующий `Dispose()` добавить отписку:
```csharp
        _sellerSession.CurrentChanged -= OnSellerChanged;
```

- [ ] **Step 5: Поставить гейт на первый товар**

Заменить `AddToCart` (около строки 736):
```csharp
    [RelayCommand]
    private void AddToCart(Product product)
    {
        // Ask who is selling when a receipt starts — matches how consultants actually
        // work: walk up, start ringing, identify. Mid-receipt it never interrupts.
        if (!_cartService.Items.Any() && _sellerSession.IsStale)
            RequestSellerSwitch?.Invoke(this, EventArgs.Empty);

        _sellerSession.Touch();
        _cartService.AddProduct(product);
    }
```
Добавить событие рядом с `NavigationRequest`:
```csharp
    public event EventHandler? RequestSellerSwitch;
```

**Сверить перед правкой:** фактическое тело `AddToCart` на строке 736 — сохранить всё, что там уже есть (в исходнике это `_cartService.AddProduct(product);` плюс возможные вызовы после него), добавив только гейт и `Touch()`.

- [ ] **Step 6: Проставить продавца в документ**

В методе `Pay`, в инициализатор `new DocumentRequest { ... }` (около строки 1053), после `DocumentHash`:
```csharp
                        SellerId = _sellerSession.Current?.Id,
```

- [ ] **Step 7: Добавить чип в разметку**

В `src/VvCash/Views/PosView.axaml`, в шапку рядом с индикатором смены:
```xml
          <Button Command="{Binding OpenSellerSwitchCommand}"
                  IsVisible="{Binding CurrentSellerName, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
                  Padding="12,6" Margin="8,0">
            <TextBlock Text="{Binding CurrentSellerName}" FontSize="14"/>
          </Button>
```
и добавить в `PosViewModel`:
```csharp
    [RelayCommand]
    private void OpenSellerSwitch() => RequestSellerSwitch?.Invoke(this, EventArgs.Empty);
```

- [ ] **Step 8: Связать оверлей в App.axaml.cs**

В `NavigateToPos()`, после создания `posVm`:
```csharp
                var switchVm = Services.GetRequiredService<SellerSwitchViewModel>();
                posVm.RequestSellerSwitch += (s, e) => switchVm.Open();
                posVm.SellerSwitchViewModel = switchVm;
```
и добавить свойство в `PosViewModel`:
```csharp
    public SellerSwitchViewModel? SellerSwitchViewModel { get; set; }
```
В `PosView.axaml` разместить оверлей последним элементом корневой панели:
```xml
    <views:SellerSwitchView DataContext="{Binding SellerSwitchViewModel}"/>
```
с объявлением `xmlns:views="using:VvCash.Views"` в корневом теге, если его там ещё нет.

- [ ] **Step 9: Написать тест на гейт**

`tests/VvCash.Tests/PosViewModelSellerGateTest.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

/// <summary>The gate rule lives here rather than inside PosViewModel (1100+ lines):
/// a receipt asks who is selling only at its first line, and only when the seller
/// went stale.</summary>
public class PosViewModelSellerGateTest
{
    private static string Encode(string pin)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        using var kdf = new Rfc2898DeriveBytes(pin, salt, 1000, HashAlgorithmName.SHA256);
        return $"pbkdf2_sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(kdf.GetBytes(32))}";
    }

    [Fact]
    public async Task StaleSeller_OnEmptyCart_NeedsSwitch()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = new SellerSession(() => now, TimeSpan.FromSeconds(90));
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-1", FirstName = "Азиз", PinHash = Encode("4821"), CanSell = true }
        });

        Assert.True(session.IsStale);

        await session.SwitchAsync("u-1", "4821");
        Assert.False(session.IsStale);

        now = now.AddSeconds(91);
        Assert.True(session.IsStale);
    }
}
```

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~PosViewModelSellerGateTest`
Expected: PASS

- [ ] **Step 10: Прогнать все тесты**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS. `SmokeTest` может падать из-за нового параметра конструктора `PosViewModel` — поправить его конструирование, передав `new SellerSession()`.

- [ ] **Step 11: Commit**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs src/VvCash/Views/PosView.axaml src/VvCash/App.axaml.cs src/VvCash/Models/Api/DocumentRequest.cs tests/VvCash.Tests/
git commit -m "feat: credit sales to the current seller and prompt at receipt start"
```

---

## Task 17: Загрузка ростера при старте смены

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs` (`OpenShiftAsync` ~184, инициализация ~395)
- Modify: `src/VvCash/Services/Data/SyncService.cs`

- [ ] **Step 1: Грузить ростер при открытии смены и при восстановлении состояния**

В `PosViewModel` внедрить `ISellerRosterService _rosterService` (поле + параметр конструктора). После успешного `OpenShiftAsync` и после `GetShiftStateAsync` (строка ~395) добавить:
```csharp
        await _sellerSession.LoadRosterAsync(await _rosterService.RefreshAsync());
```

- [ ] **Step 2: Обновлять ростер в цикле синхронизации**

Найти метод периодической синхронизации:

Run: `grep -n "public async Task" src/VvCash/Services/Data/SyncService.cs`

В теле того метода, который тянет товары и категории, после их обновления добавить:
```csharp
            // Roster changes (new hire, revoked seller, changed PIN) reach the register
            // on the same cadence as the catalogue.
            await _sellerRosterService.RefreshAsync();
```
Внедрить `ISellerRosterService _sellerRosterService` полем и параметром конструктора `SyncService`.

Вызов обёрнут в общий `try/catch` метода синхронизации, если он там есть; если нет — `RefreshAsync` уже сам не бросает (Task 11), поэтому дополнительная обработка не нужна.

- [ ] **Step 3: Собрать и прогнать тесты**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify` затем `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: Build succeeded, тесты зелёные. `SyncServiceTest` может потребовать новой заглушки — добавить `ISellerRosterService`-заглушку, возвращающую пустой список.

- [ ] **Step 4: Commit**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs src/VvCash/Services/Data/SyncService.cs tests/VvCash.Tests/
git commit -m "feat: load the seller roster on shift start and during sync"
```

---

## Task 18: Смена = срок жизни JWT

**Files:**
- Modify: `src/VvCash/Services/Api/AuthService.cs:74-77`, `src/VvCash/Constants/AuthConstants.cs`, `src/VvCash/ViewModels/PosViewModel.cs` (`DoCloseShiftAsync` ~225)

- [ ] **Step 1: Убрать 24-часовой срок из логина**

В `AuthService.LoginAsync` заменить установку срока:
```csharp
                            // The session now lives as long as the shift, not a fixed
                            // window: closing the shift clears the token. "Remember me"
                            // only decides whether the token survives an app restart.
                            _settingsService.AuthTokenExpiresAt = rememberMe
                                ? DateTime.UtcNow.AddHours(Constants.AuthConstants.MaxShiftHours)
                                : null;
```

В `src/VvCash/Constants/AuthConstants.cs` заменить константу:
```csharp
namespace VvCash.Constants;

public static class AuthConstants
{
    // Upper bound on how long a remembered session may survive. A shift is expected to
    // be closed well before this; the cap only stops a forgotten register from staying
    // authenticated indefinitely.
    public const int MaxShiftHours = 24;
}
```

- [ ] **Step 2: Стирать токен при закрытии смены**

В `PosViewModel.DoCloseShiftAsync`, внутри ветки успешного закрытия (после `CurrentShiftId = null;`):
```csharp
            _sellerSession.Clear();
            _settingsService.AuthToken = string.Empty;
            _settingsService.AuthTokenExpiresAt = null;
            _settingsService.Save();
```
Понадобится внедрить `ISettingsService` в `PosViewModel`, если его там ещё нет — проверить список полей конструктора.

- [ ] **Step 3: Требовать право на закрытие смены**

В `PosViewModel.CloseShift` (около строки 199) перед подтверждением: если `_sellerSession.Current?.CanCloseShift != true`, открыть оверлей в режиме подтверждения через новое событие:
```csharp
    public event EventHandler? RequestShiftCloseApproval;
```
и подписать его в `App.axaml.cs` рядом с `RequestSellerSwitch`:
```csharp
                posVm.RequestShiftCloseApproval += (s, e) => switchVm.OpenForApproval(x => x.CanCloseShift);
```

Механизм возобновления операции после подтверждения добавляется в Task 21. До него закрытие смены после ввода PIN не продолжается автоматически — на этом шаге достаточно того, что оверлей открывается и отсеивает сотрудников без права. В Task 21 эта подписка заменяется на вариант с продолжением:
```csharp
                posVm.RequestShiftCloseApproval += (s, e) => switchVm.OpenForApproval(
                    x => x.CanCloseShift,
                    _ => posVm.ConfirmCloseShiftCommand.ExecuteAsync(null));
```

- [ ] **Step 4: Прогнать тесты**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS. `SettingsDefaultsTest` может ссылаться на `RememberLoginHours` — обновить на `MaxShiftHours`.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/
git commit -m "feat: tie the auth token lifetime to the shift and gate shift close"
```

---

## Task 19: Первичная установка PIN

**Files:**
- Modify: `src/VvCash/Services/Api/ISellerRosterService.cs`, `SellerRosterService.cs`, `src/VvCash/ViewModels/SellerSwitchViewModel.cs`

- [ ] **Step 1: Добавить вызов установки PIN**

В `ISellerRosterService`:
```csharp
    /// <summary>Sets the PIN for a seller who has none yet. Requires network.</summary>
    Task<bool> SetPinAsync(string sellerId, string pin);
```

В `SellerRosterService`:
```csharp
    public async Task<bool> SetPinAsync(string sellerId, string pin)
    {
        try
        {
            var baseUrl = _settingsService.BackendUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return false;
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}users/pin/reset/", new { user = sellerId, pin });
            if (!response.IsSuccessStatusCode) return false;

            await RefreshAsync();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SellerRosterService] SetPin failed: {ex.Message}");
            return false;
        }
    }
```
Добавить `using System.Net.Http.Json;`.

Используется `reset`, а не `POST /users/pin/`, потому что PIN задаётся на кассе от имени владельца смены для другого сотрудника; вызывающий должен иметь право `users.pin_reset`.

- [ ] **Step 2: Ветка «PIN не задан» в оверлее**

В `SellerSwitchViewModel` при `SwitchResult.PinNotSet` вместо ошибки переключать в режим создания PIN: два ввода по четыре цифры, сравнение, затем `SetPinAsync`. Состояние — `[ObservableProperty] private bool _isPinSetup;` и `private string _firstEntry = string.Empty;`. При офлайне (`SetPinAsync` вернул `false`) показывать `I18nService.Instance["SellerPinSetupOffline"]` и закрывать оверлей без смены продавца — работает деградация «продавец = владелец смены».

Добавить ключи локализации во все пять файлов `src/VvCash/Assets/i18n/`:
```json
  "CreatePin": "Придумайте PIN",
  "RepeatPin": "Повторите PIN",
  "PinMismatch": "PIN не совпал",
  "SellerPinSetupOffline": "Нет связи — задайте PIN позже",
```

- [ ] **Step 3: Прогнать тесты и собрать**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify` затем `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: Build succeeded, все тесты зелёные

- [ ] **Step 4: Commit**

```bash
git add src/VvCash/
git commit -m "feat: let a seller without a PIN set one at first switch"
```

---

## Task 20: Гейт возвратов по `can_refund`

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs` (команда открытия возвратов)

- [ ] **Step 1: Найти точку входа в возвраты**

Run: `grep -n "Return" src/VvCash/ViewModels/PosViewModel.cs`
Expected: команда, открывающая `ReturnsWindow` — её точное имя определяется этим поиском.

- [ ] **Step 2: Поставить проверку права**

В найденной команде, перед открытием окна:
```csharp
        if (_sellerSession.Current?.CanRefund != true)
        {
            RequestRefundApproval?.Invoke(this, EventArgs.Empty);
            return;
        }
```
и добавить событие рядом с `RequestSellerSwitch`:
```csharp
    public event EventHandler? RequestRefundApproval;
```

- [ ] **Step 3: Подписать эскалацию**

В `App.axaml.cs`, в `NavigateToPos()`, рядом с прочими подписками на `switchVm`:
```csharp
                posVm.RequestRefundApproval += (s, e) => switchVm.OpenForApproval(x => x.CanRefund);
```

Продолжение после подтверждения обрабатывается общим механизмом из Task 21 — сначала выполнить Task 21, затем вернуться и подключить продолжение здесь.

- [ ] **Step 4: Собрать и прогнать**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify` затем `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: Build succeeded, тесты зелёные

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs src/VvCash/App.axaml.cs
git commit -m "feat: require the refund right to open returns"
```

---

## Task 21: Продолжение после подтверждения + `approved_by` в чеке

Оверлей подтверждения должен уметь возобновить прерванную операцию. Без этого эскалация из Task 18 и Task 20 остаётся половинчатой.

**Files:**
- Modify: `src/VvCash/ViewModels/SellerSwitchViewModel.cs`
- Modify: `src/VvCash/ViewModels/PosViewModel.cs`
- Modify: `src/VvCash/App.axaml.cs`
- Test: `tests/VvCash.Tests/SellerSwitchApprovalTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/SellerSwitchApprovalTest.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class SellerSwitchApprovalTest
{
    private static string Encode(string pin)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        using var kdf = new Rfc2898DeriveBytes(pin, salt, 1000, HashAlgorithmName.SHA256);
        return $"pbkdf2_sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(kdf.GetBytes(32))}";
    }

    private static async Task<SellerSession> Session()
    {
        var s = new SellerSession(() => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(90));
        await s.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-1", FirstName = "Азиз", PinHash = Encode("4821"), CanSell = true },
            new() { Id = "u-2", FirstName = "Дилноза", PinHash = Encode("9073"), CanSell = true, CanRefund = true }
        });
        return s;
    }

    [Fact]
    public async Task ApprovedContinuation_RunsWithApprover()
    {
        var vm = new SellerSwitchViewModel(await Session());
        SellerInfo? seen = null;

        vm.OpenForApproval(x => x.CanRefund, approver => { seen = approver; return Task.CompletedTask; });
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "9073")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.Equal("u-2", seen?.Id);
    }

    [Fact]
    public async Task WrongApprovalPin_DoesNotRunContinuation()
    {
        var vm = new SellerSwitchViewModel(await Session());
        var ran = false;

        vm.OpenForApproval(x => x.CanRefund, _ => { ran = true; return Task.CompletedTask; });
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "0000")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.False(ran);
        Assert.True(vm.HasError);
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj --filter FullyQualifiedName~SellerSwitchApprovalTest`
Expected: FAIL — у `OpenForApproval` нет второго параметра

- [ ] **Step 3: Добавить продолжение в VM**

В `SellerSwitchViewModel` заменить перегрузку и хранить продолжение:
```csharp
    private Func<SellerInfo, Task>? _onApproved;

    public void OpenForApproval(Func<SellerInfo, bool> hasRight, Func<SellerInfo, Task>? onApproved = null)
    {
        _onApproved = onApproved;
        Show(hasRight, approvalMode: true);
    }
```
и в `SubmitAsync`, в ветке успешного подтверждения, заменить вызов события на:
```csharp
            IsVisible = false;
            Approved?.Invoke(this, approver);
            if (_onApproved != null) await _onApproved(approver);
            return;
```
В `Show(...)` сбрасывать `_onApproved` только когда открывают обычный выбор:
```csharp
        if (!approvalMode) _onApproved = null;
```

- [ ] **Step 4: Передать подтвердившего в документ**

В `PosViewModel` добавить поле, которое живёт до конца текущего чека:
```csharp
    // Set when a supervisor approved an operation beyond the current seller's rights.
    // Cleared together with the cart, so it never leaks into the next receipt.
    private string? _approvedById;
```
В инициализатор `DocumentRequest` (Task 16, Step 6) добавить:
```csharp
                        ApprovedBy = _approvedById,
```
и в `DocumentRequest` (`src/VvCash/Models/Api/DocumentRequest.cs`) добавить поле:
```csharp
    [JsonPropertyName("approved_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApprovedBy { get; set; }
```
Сбрасывать `_approvedById = null;` там же, где очищается корзина после успешной оплаты.

- [ ] **Step 5: Подключить эскалацию скидки**

**`MaxDiscount == 0` означает «персональный потолок не задан» — гейта нет.** Не «скидка запрещена». Сразу после миграции потолок не задан ни у кого, и обратная трактовка потребовала бы PIN старшего на каждую ручную скидку с первого дня. Подробности — в спеке, раздел про `cash_users.max_discount`.

В команде применения ручной скидки:
```csharp
    private bool NeedsDiscountApproval(decimal percent)
    {
        var cap = _sellerSession.Current?.MaxDiscount ?? 0m;
        return cap > 0m && percent > cap;
    }
```
Если `NeedsDiscountApproval(...)` — вместо применения вызвать
```csharp
        RequestDiscountApproval?.Invoke(this, EventArgs.Empty);
```
с событием
```csharp
    public event EventHandler? RequestDiscountApproval;
```
и подпиской в `App.axaml.cs` (подтвердить может тот, у кого потолок задан и покрывает запрошенный процент):
```csharp
                posVm.RequestDiscountApproval += (s, e) => switchVm.OpenForApproval(
                    x => x.MaxDiscount > 0m && x.MaxDiscount >= posVm.PendingDiscountPercent,
                    approver => { posVm.ApplyApprovedDiscount(approver.Id); return Task.CompletedTask; });
```
Добавить в `PosViewModel` свойство `PendingDiscountPercent` (процент, который пытались применить) и метод:
```csharp
    public void ApplyApprovedDiscount(string approverId)
    {
        _approvedById = approverId;
        _cartService.SetManualDiscountPercent(PendingDiscountPercent);
    }
```
**Сверить:** фактическое имя метода установки ручной скидки в `ICartService` — взять из `src/VvCash/Services/CartService.cs`, там уже есть `ManualDiscountPercent`.

- [ ] **Step 6: Запустить тесты**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS, включая два новых теста

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/ tests/VvCash.Tests/SellerSwitchApprovalTest.cs
git commit -m "feat: resume the approved operation and record who approved it"
```

---

## Task 22: Баннер при отозванной сессии смены

Спека требует: 401 при синхронизации не выбрасывает на экран логина посреди чека — чеки продолжают копиться.

**Files:**
- Modify: `src/VvCash/Services/Api/ExpenseDocumentService.cs:105-140` (`SyncOfflineDocumentsAsync`)
- Modify: `src/VvCash/ViewModels/PosViewModel.cs`, `src/VvCash/Views/PosView.axaml`

- [ ] **Step 1: Сообщать об отозванной сессии**

В `ExpenseDocumentService` добавить событие рядом с `UnsyncedDocumentsCountChanged`:
```csharp
    /// <summary>Raised when the server rejected the shift session (HTTP 401). The register
    /// keeps queueing receipts; only a banner is shown, never a forced logout mid-receipt.</summary>
    public event EventHandler? SessionRevoked;
```
и в `SyncOfflineDocumentsAsync`, при обработке ответа:
```csharp
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            SessionRevoked?.Invoke(this, EventArgs.Empty);
                            break;
                        }
```
`break` вместо `continue` — при отозванном токене остальные документы тоже не пройдут, смысла долбить сервер нет.

Добавить событие в `IExpenseDocumentService`.

- [ ] **Step 2: Показать баннер**

В `PosViewModel` добавить свойство и подписку:
```csharp
    [ObservableProperty] private bool _isSessionRevoked;
```
```csharp
        _expenseDocumentService.SessionRevoked += OnSessionRevoked;
```
```csharp
    private void OnSessionRevoked(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSessionRevoked = true);
```
и отписку в `Dispose()`.

- [ ] **Step 3: Вывести в разметке**

В `src/VvCash/Views/PosView.axaml`, вверху корневой панели:
```xml
    <Border Background="#FFF3CD" Padding="12" IsVisible="{Binding IsSessionRevoked}">
      <TextBlock Text="{Binding Source={x:Static vm:I18nBinding.Instance}, Path=[SessionRevokedBanner]}"
                 Foreground="#664D03"/>
    </Border>
```
Способ локализации в разметке — повторить фактический из соседних элементов `PosView.axaml`.

Добавить ключ во все пять файлов `src/VvCash/Assets/i18n/`. Русский:
```json
  "SessionRevokedBanner": "Сессия смены недействительна. Чеки сохраняются, войдите заново после текущего клиента.",
```
Английский:
```json
  "SessionRevokedBanner": "Shift session is no longer valid. Receipts are being saved — sign in again after this customer.",
```

- [ ] **Step 4: Собрать и прогнать**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify` затем `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: Build succeeded, тесты зелёные

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/
git commit -m "feat: show a banner instead of forcing logout on a revoked shift session"
```

---

## Task 23: Финальная проверка

- [ ] **Step 1: Полный прогон тестов клиента**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: все тесты PASS, ноль падений

- [ ] **Step 2: Полный прогон тестов бэкенда**

Из `C:\work\cloudmarket-server`:
Run: `go build ./... && go test ./...`
Expected: сборка проходит, тесты PASS

- [ ] **Step 3: Ручной сценарий**

Запустить кассу, проверить руками:
1. Логин по email+паролю → открытие смены → появился оверлей выбора продавца
2. Выбор продавца, неверный PIN пять раз → плитка блокируется
3. Верный PIN другого продавца → чип в шапке показывает его имя
4. Добавление товара в пустую корзину после 90 секунд простоя → оверлей всплыл снова
5. Продажа → в БД `document_expenses.seller_id` равен выбранному продавцу, не владельцу смены
6. Отключить сеть → переключение продавца всё ещё работает, чек уходит в очередь
7. Закрытие смены → требует PIN сотрудника с правом, после закрытия приложение просит полный логин

- [ ] **Step 4: Commit при необходимости**

Если ручная проверка выявила правки — исправить, прогнать тесты, закоммитить.
