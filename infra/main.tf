data "aws_ssm_parameter" "connection_string" {
  name            = "/${var.project_name}/${var.environment}/database/os-connection-string"
  with_decryption = true
}

resource "kubernetes_secret_v1" "database" {
  metadata {
    name      = "os-database"
    namespace = "garage-microservices"
  }
  data = { "connection-string" = data.aws_ssm_parameter.connection_string.value }
  type = "Opaque"
}

resource "kubernetes_deployment_v1" "service" {
  metadata {
    name      = "os-service"
    namespace = "garage-microservices"
    labels    = { app = "os-service" }
  }
  spec {
    replicas                  = var.replicas
    progress_deadline_seconds = 300
    selector { match_labels = { app = "os-service" } }
    strategy {
      type = "RollingUpdate"
      rolling_update {
        max_surge       = "1"
        max_unavailable = "0"
      }
    }
    template {
      metadata { labels = { app = "os-service" } }
      spec {
        container {
          name  = "os-service"
          image = var.container_image
          port { container_port = 8080 }
          env {
            name = "ConnectionStrings__ServiceOrders"
            value_from {
              secret_key_ref {
                name = kubernetes_secret_v1.database.metadata[0].name
                key  = "connection-string"
              }
            }
          }
          env {
            name  = "Services__BillingUrl"
            value = "http://billing-service:8080"
          }
          env {
            name  = "Services__ExecutionUrl"
            value = "http://execution-service:8080"
          }
          readiness_probe {
            http_get {
              path = "/health"
              port = 8080
            }
            initial_delay_seconds = 5
            period_seconds        = 5
            timeout_seconds       = 3
            failure_threshold     = 6
          }
          liveness_probe {
            http_get {
              path = "/health"
              port = 8080
            }
            initial_delay_seconds = 15
            period_seconds        = 20
            timeout_seconds       = 3
            failure_threshold     = 3
          }
        }
      }
    }
  }
}

resource "kubernetes_service_v1" "service" {
  metadata {
    name      = "os-service"
    namespace = "garage-microservices"
  }
  spec {
    selector = { app = "os-service" }
    port {
      port        = 8080
      target_port = 8080
    }
  }
}
