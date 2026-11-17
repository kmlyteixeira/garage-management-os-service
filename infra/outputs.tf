output "service_name" { value = kubernetes_service_v1.service.metadata[0].name }
