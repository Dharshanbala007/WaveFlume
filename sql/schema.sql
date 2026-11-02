-- WaveFlume SCADA schema (spec §12). MySQL 8.0.16+ (CHECK constraints). Timestamps are UTC.
-- DatabaseService.InitializeSchemaAsync runs this against the configured database and skips the
-- CREATE DATABASE / USE lines, so the same script also serves a test database.

CREATE DATABASE IF NOT EXISTS wave_flume_scada CHARACTER SET utf8mb4;
USE wave_flume_scada;

CREATE TABLE IF NOT EXISTS system_settings (
  setting_id            INT PRIMARY KEY DEFAULT 1,
  paddle_type           ENUM('Piston','Flap') NOT NULL DEFAULT 'Piston',
  hinge_height_m        FLOAT NOT NULL DEFAULT 0.0,
  max_stroke_mm         FLOAT NOT NULL DEFAULT 350.0 COMMENT 'operational +/- clamp; ceiling 450 enforced in code',
  max_velocity_mms      FLOAT NOT NULL DEFAULT 1570.0,
  max_accel_mms2        FLOAT NULL COMMENT 'NULL = not configured; Start disabled',
  paddle_width_m        FLOAT NOT NULL DEFAULT 0.5,
  num_paddles           INT   NOT NULL DEFAULT 6,
  max_water_depth_m     FLOAT NOT NULL DEFAULT 0.8,
  t_min_s               FLOAT NOT NULL DEFAULT 0.6,
  t_max_s               FLOAT NOT NULL DEFAULT 6.0,
  command_fs_hz         FLOAT NOT NULL DEFAULT 125.0,
  telemetry_rate_hz     FLOAT NOT NULL DEFAULT 125.0,
  plc_protocol          VARCHAR(50) NOT NULL DEFAULT 'Omron_CIP',
  plc_ip_address        VARCHAR(45) NOT NULL DEFAULT '192.168.250.1',
  plc_port              INT NOT NULL DEFAULT 44818,
  plc_extras_json       JSON NULL COMMENT 'unit id, rack/slot, FINS nodes, AMS NetID, byte order',
  tagmap_profile        VARCHAR(100) NULL,
  daq_ip_address        VARCHAR(45) NULL DEFAULT '192.168.169.5',
  calibration_factor    FLOAT NOT NULL DEFAULT 1.0,
  updated_at            DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  CONSTRAINT chk_single_row CHECK (setting_id = 1)
);

CREATE TABLE IF NOT EXISTS wave_recipes (
  recipe_id            INT AUTO_INCREMENT PRIMARY KEY,
  test_number          VARCHAR(50) NOT NULL UNIQUE,
  wave_order           INT NOT NULL DEFAULT 1 COMMENT '1 or 2',
  wave_type            VARCHAR(50) NOT NULL COMMENT 'Monochromatic, Bichromatic, Random, Cnoidal, Solitary, Focusing, UserDefinedWave, UserDefinedStroke',
  spectral_shape       VARCHAR(50) DEFAULT 'None' COMMENT 'PM, GeneralizedPM, JONSWAP, TMA, Darbyshire',
  random_seed          INT DEFAULT 12345,
  gamma_factor         FLOAT DEFAULT 3.3,
  beta_factor          FLOAT DEFAULT 1.25,
  water_depth_m        FLOAT NOT NULL DEFAULT 0.6,
  signal_wave_height_m FLOAT NOT NULL DEFAULT 0.05,
  peak_wave_period_s   FLOAT NOT NULL DEFAULT 1.0,
  wave_angle_deg       FLOAT NOT NULL DEFAULT 0.0,
  num_waves            INT NOT NULL DEFAULT 10,
  sampling_freq_hz     FLOAT NOT NULL DEFAULT 125.0,
  signal_mod_pct       FLOAT NOT NULL DEFAULT 100.0,
  ramp_time_s          FLOAT NOT NULL DEFAULT 2.0,
  extra_params_json    JSON NULL COMMENT 'bichromatic H2/T2, focusing xf/tf, band limits, user-file refs',
  calc_stroke_m        FLOAT, calc_velocity_ms FLOAT, calc_accel_ms2 FLOAT,
  calc_force_kn        FLOAT, calc_power_w FLOAT, calc_wavelength_m FLOAT, calc_celerity_ms FLOAT,
  created_at           DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS test_runs (
  run_id                  INT AUTO_INCREMENT PRIMARY KEY,
  recipe_id               INT NOT NULL,
  start_timestamp         DATETIME(3) NOT NULL,
  end_timestamp           DATETIME(3) NULL,
  extra_record_duration_s FLOAT DEFAULT 1.0,
  run_status              ENUM('Running','Completed','E-Stopped','Fault') NOT NULL DEFAULT 'Running',
  h_1_3_m FLOAT, h_10_m FLOAT, h_max_m FLOAT, t_1_3_s FLOAT, t_10_s FLOAT, t_max_s FLOAT,
  limits_snapshot_json    JSON NULL COMMENT 'clamps, PLC profile and app version at run time',
  csv_file_path           VARCHAR(500) NOT NULL,
  pdf_report_path         VARCHAR(500) NULL,
  INDEX ix_run_recipe (recipe_id),
  CONSTRAINT fk_run_recipe FOREIGN KEY (recipe_id) REFERENCES wave_recipes(recipe_id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS telemetry_alerts (
  alert_id       INT AUTO_INCREMENT PRIMARY KEY,
  run_id         INT NULL,
  timestamp      DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  alert_severity ENUM('Warning','Critical','E-Stop') NOT NULL,
  alert_source   VARCHAR(50) NOT NULL COMMENT 'MathGatekeeper, PLC_Status, ServoDrive, WaveProbe, Comms',
  description    VARCHAR(500) NOT NULL,
  INDEX ix_alert_run_time (run_id, timestamp),
  CONSTRAINT fk_alert_run FOREIGN KEY (run_id) REFERENCES test_runs(run_id) ON DELETE SET NULL
);
